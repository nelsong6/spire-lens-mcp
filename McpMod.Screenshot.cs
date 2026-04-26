using System;
using System.Collections.Generic;
using System.Net;
using Godot;

namespace SpireLens.Mcp;

public static partial class McpMod
{
    private static void HandleGetScreenshot(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var screenshotTask = RunOnMainThread(CaptureRootViewportPng);
            var screenshot = screenshotTask.GetAwaiter().GetResult();
            SendJson(response, screenshot);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireLens MCP] HandleGetScreenshot: {ex}");
            try
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to capture root viewport screenshot: {ex.Message}",
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace
                });
            }
            catch { /* response may be unusable */ }
        }
    }

    private static Dictionary<string, object?> CaptureRootViewportPng()
    {
        var tree = Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Godot main loop is not a SceneTree.");
        var root = tree.Root
            ?? throw new InvalidOperationException("SceneTree root viewport is unavailable.");
        var texture = root.GetTexture()
            ?? throw new InvalidOperationException("Root viewport texture is unavailable.");
        var image = texture.GetImage()
            ?? throw new InvalidOperationException("Root viewport image is unavailable.");

        byte[] png = image.SavePngToBuffer();
        if (png.Length == 0)
            throw new InvalidOperationException("Root viewport produced an empty PNG.");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["target"] = "godot_root_viewport",
            ["width"] = image.GetWidth(),
            ["height"] = image.GetHeight(),
            ["png_base64"] = Convert.ToBase64String(png)
        };
    }
}
