using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLens.Mcp;

public static partial class McpMod
{
    private static bool IsDevAction(string action) => action.StartsWith("dev_", StringComparison.Ordinal);

    private static Dictionary<string, object?> ExecuteDevAction(string action, Dictionary<string, JsonElement> data)
        => action switch
        {
            "dev_reload_spirelens_core" => ExecuteDevReloadSpireLensCore(),
            "dev_start_singleplayer_run" => ExecuteDevStartSingleplayerRun(data),
            "dev_enter_room" => ExecuteDevEnterRoom(data),
            "dev_configure_test_combat" => ExecuteDevConfigureTestCombat(data),
            _ => Error($"Unknown dev action: {action}")
        };

    private static Dictionary<string, object?> ExecuteDevReloadSpireLensCore()
    {
        var loaderType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SpireLens.Loader.LoaderMain", throwOnError: false))
            .FirstOrDefault(t => t != null);

        if (loaderType == null)
            return Error("SpireLens loader was not found in the current AppDomain.");

        var reloadMethod = loaderType.GetMethod("ReloadCore", BindingFlags.Public | BindingFlags.Static);
        if (reloadMethod == null)
            return Error("SpireLens loader does not expose public static ReloadCore().");

        reloadMethod.Invoke(null, null);

        object? reloadNumber = null;
        var reloadNumberProperty = loaderType.GetProperty("ReloadNumber", BindingFlags.Public | BindingFlags.Static);
        if (reloadNumberProperty != null)
            reloadNumber = reloadNumberProperty.GetValue(null);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Requested SpireLens Core hot reload.",
            ["reload_number"] = reloadNumber
        };
    }

    private static Dictionary<string, object?> ExecuteDevStartSingleplayerRun(Dictionary<string, JsonElement> data)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");
        if (NGame.Instance == null)
            return Error("NGame.Instance is not available yet.");

        string characterId = GetString(data, "character", "Ironclad");
        int ascension = GetInt(data, "ascension", 0);
        string seed = GetString(data, "seed", SeedHelper.GetRandomSeed());

        var character = ModelDb.AllCharacters.FirstOrDefault(c =>
            c.Id.Entry.Equals(characterId, StringComparison.OrdinalIgnoreCase)
            || (SafeGetText(() => c.Title)?.Equals(characterId, StringComparison.OrdinalIgnoreCase) ?? false));
        if (character == null)
            return Error($"Unknown character '{characterId}'.");

        var acts = ModelDb.Acts.ToList();
        if (acts.Count == 0)
            return Error("No acts are registered in ModelDb.");

        TaskHelper.RunSafely(NGame.Instance.StartNewSingleplayerRun(
            character,
            shouldSave: false,
            acts,
            Array.Empty<ModifierModel>(),
            SeedHelper.CanonicalizeSeed(seed),
            GameMode.Standard,
            ascension));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting singleplayer run as {SafeGetText(() => character.Title) ?? character.Id.Entry}.",
            ["character"] = character.Id.Entry,
            ["ascension"] = ascension,
            ["seed"] = SeedHelper.CanonicalizeSeed(seed)
        };
    }

    private static Dictionary<string, object?> ExecuteDevEnterRoom(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");

        string roomTypeValue = GetString(data, "room_type", "monster");
        if (!Enum.TryParse<RoomType>(roomTypeValue, ignoreCase: true, out var roomType))
            return Error($"Unknown room_type '{roomTypeValue}'.");

        TaskHelper.RunSafely(RunManager.Instance.EnterRoomDebug(
            roomType,
            MapPointType.Unassigned,
            model: null,
            showTransition: false));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Entering debug room: {roomType}."
        };
    }

    private static Dictionary<string, object?> ExecuteDevConfigureTestCombat(Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress.");
        if (!CombatManager.Instance.IsInProgress)
            return Error("No combat in progress. Call start_singleplayer_run and enter_debug_room first.");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("Run state is unavailable.");

        var player = LocalContext.GetMe(runState);
        if (player?.PlayerCombatState == null)
            return Error("Player combat state is unavailable.");

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return Error("Combat state is unavailable.");

        int enemyHp = GetInt(data, "enemy_hp", 999);
        bool clearHand = GetBool(data, "clear_hand", true);
        bool clearDraw = GetBool(data, "clear_draw", true);
        bool clearDiscard = GetBool(data, "clear_discard", true);
        bool clearExhaust = GetBool(data, "clear_exhaust", true);

        var handCards = GetStringArray(data, "hand");
        var drawCards = GetStringArray(data, "draw_pile");
        var discardCards = GetStringArray(data, "discard_pile");
        var exhaustCards = GetStringArray(data, "exhaust_pile");
        var deckCards = GetStringArray(data, "deck");

        if (deckCards.Count > 0)
        {
            ClearPile(player.Deck);
            AddCardsToDeck(runState, player, deckCards);
        }
        if (clearHand) ClearPile(player.PlayerCombatState.Hand);
        if (clearDraw) ClearPile(player.PlayerCombatState.DrawPile);
        if (clearDiscard) ClearPile(player.PlayerCombatState.DiscardPile);
        if (clearExhaust) ClearPile(player.PlayerCombatState.ExhaustPile);

        var added = new Dictionary<string, object?>
        {
            ["deck"] = deckCards.Count > 0 ? BuildCardList(player.Deck.Cards) : null,
            ["hand"] = AddCardsToPile(combatState, player, player.PlayerCombatState.Hand, handCards),
            ["draw_pile"] = AddCardsToPile(combatState, player, player.PlayerCombatState.DrawPile, drawCards),
            ["discard_pile"] = AddCardsToPile(combatState, player, player.PlayerCombatState.DiscardPile, discardCards),
            ["exhaust_pile"] = AddCardsToPile(combatState, player, player.PlayerCombatState.ExhaustPile, exhaustCards)
        };

        if (TryGetNullableInt(data, "energy", out var energy) && energy.HasValue)
            player.PlayerCombatState.Energy = energy.Value;
        if (TryGetNullableInt(data, "stars", out var stars) && stars.HasValue)
            player.PlayerCombatState.Stars = stars.Value;

        var enemies = new List<Dictionary<string, object?>>();
        var livingEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();
        foreach (var enemy in livingEnemies)
        {
            SetCreatureHp(enemy, enemyHp);
            enemies.Add(new Dictionary<string, object?>
            {
                ["name"] = SafeGetText(() => enemy.Monster?.Title),
                ["combat_id"] = enemy.CombatId,
                ["hp"] = enemy.CurrentHp,
                ["max_hp"] = enemy.MaxHp
            });
        }

        var playerPowers = ApplyPowerSpecs(data, "player_powers", [player.Creature]);
        var enemyPowers = ApplyPowerSpecs(data, "enemy_powers", livingEnemies);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Configured current combat for deterministic test validation.",
            ["default_fixture"] = "single durable early/debug monster, high HP, controlled piles",
            ["enemy_hp"] = enemyHp,
            ["energy"] = player.PlayerCombatState.Energy,
            ["stars"] = player.PlayerCombatState.Stars,
            ["enemies"] = enemies,
            ["added"] = added,
            ["player_powers"] = playerPowers,
            ["enemy_powers"] = enemyPowers,
            ["next_step"] = "Call get_game_state, then capture target-visible screenshot evidence."
        };
    }

    private static void ClearPile(CardPile pile)
    {
        foreach (var card in pile.Cards.ToList())
            pile.RemoveInternal(card, true);
    }

    private static void SetCreatureHp(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, int hp)
    {
        var type = creature.GetType();
        type.GetMethod("SetMaxHpInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(creature, new object[] { (decimal)hp });
        type.GetMethod("SetCurrentHpInternal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(creature, new object[] { (decimal)hp });
    }

    private static List<Dictionary<string, object?>> AddCardsToDeck(
        MegaCrit.Sts2.Core.Runs.RunState runState,
        Player player,
        IReadOnlyList<string> cardIds)
    {
        var added = new List<Dictionary<string, object?>>();
        foreach (var cardId in cardIds)
        {
            var canonical = FindCardById(cardId);
            if (canonical == null)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "not_found"
                });
                continue;
            }

            var card = runState.CreateCard(canonical, player);
            player.Deck.AddInternal(card, player.Deck.Cards.Count, true);
            added.Add(new Dictionary<string, object?>
            {
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["status"] = "ok"
            });
        }
        return added;
    }

    private static List<Dictionary<string, object?>> AddCardsToPile(
        ICombatState combatState,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        CardPile pile,
        IReadOnlyList<string> cardIds)
    {
        var added = new List<Dictionary<string, object?>>();
        foreach (var cardId in cardIds)
        {
            var canonical = FindDeckCardById(player, cardId) ?? FindCardById(cardId);
            if (canonical == null)
            {
                added.Add(new Dictionary<string, object?>
                {
                    ["id"] = cardId,
                    ["status"] = "not_found"
                });
                continue;
            }

            var card = combatState.CreateCard(canonical, player);
            pile.AddInternal(card, pile.Cards.Count, true);
            added.Add(new Dictionary<string, object?>
            {
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["status"] = "ok"
            });
        }
        return added;
    }

    private static List<Dictionary<string, object?>> BuildCardList(IEnumerable<CardModel> cards)
        => cards.Select(card => new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title)
        }).ToList();

    private static CardModel? FindDeckCardById(Player player, string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        return player.Deck.Cards.FirstOrDefault(card =>
            card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
            || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static CardModel? FindCardById(string cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return null;
        foreach (var card in GetCatalogCards().OfType<CardModel>())
        {
            if (card.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase)
                || (SafeGetText(() => card.Title)?.Equals(cardId, StringComparison.OrdinalIgnoreCase) ?? false))
                return card;
        }
        return null;
    }

    private static List<Dictionary<string, object?>> ApplyPowerSpecs(
        Dictionary<string, JsonElement> data,
        string key,
        IReadOnlyList<Creature> targets)
    {
        var result = new List<Dictionary<string, object?>>();
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var spec in elem.EnumerateArray())
        {
            if (spec.ValueKind != JsonValueKind.Object)
                continue;

            string power = GetString(spec, "power", "");
            int amount = GetInt(spec, "amount", 1);
            int targetIndex = GetInt(spec, "target_index", -1);
            var selectedTargets = targetIndex >= 0 && targetIndex < targets.Count
                ? new[] { targets[targetIndex] }
                : targets;

            foreach (var target in selectedTargets)
            {
                result.Add(ApplyPower(target, power, amount));
            }
        }
        return result;
    }

    private static Dictionary<string, object?> ApplyPower(Creature target, string powerName, int amount)
    {
        var powerType = FindPowerType(powerName);
        if (powerType == null)
            return new Dictionary<string, object?>
            {
                ["power"] = powerName,
                ["status"] = "not_found"
            };

        if (Activator.CreateInstance(powerType) is not PowerModel power)
            return new Dictionary<string, object?>
            {
                ["power"] = powerName,
                ["status"] = "create_failed"
            };

        try
        {
            power.ApplyInternal(target, amount, true);
            return new Dictionary<string, object?>
            {
                ["power"] = powerType.Name,
                ["target"] = SafeGetText(() => target.Monster?.Title) ?? "player",
                ["amount"] = amount,
                ["status"] = "ok"
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["power"] = powerType.Name,
                ["amount"] = amount,
                ["status"] = "error",
                ["error"] = ex.Message
            };
        }
    }

    private static Type? FindPowerType(string powerName)
    {
        if (string.IsNullOrWhiteSpace(powerName)) return null;
        string normalized = NormalizeCatalogKey(powerName);
        if (!normalized.EndsWith("power", StringComparison.Ordinal))
            normalized += "power";

        return typeof(PowerModel).Assembly.GetTypes()
            .FirstOrDefault(type =>
                typeof(PowerModel).IsAssignableFrom(type)
                && !type.IsAbstract
                && NormalizeCatalogKey(type.Name) == normalized);
    }

    private static string GetString(Dictionary<string, JsonElement> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return fallback;
        var value = elem.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(Dictionary<string, JsonElement> data, string key, int fallback)
        => data.TryGetValue(key, out var elem) && elem.TryGetInt32(out var value) ? value : fallback;

    private static int GetInt(JsonElement data, string key, int fallback)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var elem)
           && elem.TryGetInt32(out var value)
            ? value
            : fallback;

    private static string GetString(JsonElement data, string key, string fallback)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return fallback;
        var value = elem.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool TryGetNullableInt(Dictionary<string, JsonElement> data, string key, out int? value)
    {
        value = null;
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return false;
        if (!elem.TryGetInt32(out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool GetBool(Dictionary<string, JsonElement> data, string key, bool fallback)
        => data.TryGetValue(key, out var elem) && elem.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? elem.GetBoolean()
            : fallback;

    private static IReadOnlyList<string> GetStringArray(Dictionary<string, JsonElement> data, string key)
    {
        if (!data.TryGetValue(key, out var elem) || elem.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return elem.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }
}
