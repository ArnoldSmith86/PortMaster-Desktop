using System.Text.Json;
using PortMasterDesktop.Models;

namespace PortMasterDesktop.PortMaster;

/// <summary>
/// CLI test to verify PortMaster screenshot URLs are loaded and aspect ratio is correct.
/// Usage: dotnet run -- test-portmaster-images
/// </summary>
public class TestPortMasterImages
{
    public static async Task RunTestAsync(PortMasterClient client)
    {
        Console.WriteLine("🔍 Testing PortMaster Screenshots...\n");

        var ports = await client.GetPortsAsync();
        Console.WriteLine($"Total ports loaded: {ports.Count}\n");

        var portsWithScreenshots = ports.Where(p => !string.IsNullOrEmpty(p.ScreenshotUrl)).ToList();
        Console.WriteLine($"Ports with ScreenshotUrl: {portsWithScreenshots.Count}\n");

        // Show first 10 examples
        Console.WriteLine("Sample ports with screenshots:");
        foreach (var port in portsWithScreenshots.Take(10))
        {
            Console.WriteLine($"  • {port.Attr.Title}");
            Console.WriteLine($"    Slug: {port.Slug}");
            Console.WriteLine($"    ScreenshotUrl: {port.ScreenshotUrl}");
            Console.WriteLine();
        }

        // Test aspect ratio calculation
        Console.WriteLine("\nTesting aspect ratio calculation:");
        var gameMatch = new GameMatch
        {
            Port = portsWithScreenshots.FirstOrDefault(),
            UsePortMasterImages = true
        };

        if (gameMatch.Port != null)
        {
            Console.WriteLine($"Port: {gameMatch.Port.Attr.Title}");
            Console.WriteLine($"ScreenshotUrl: {gameMatch.Port.ScreenshotUrl}");
            Console.WriteLine($"UsePortMasterImages: {gameMatch.UsePortMasterImages}");
            Console.WriteLine($"DisplayCoverUrl: {gameMatch.DisplayCoverUrl}");
            Console.WriteLine($"DisplayImageAspectRatio: {gameMatch.DisplayImageAspectRatio} (4:3 = 1.333)");
            Console.WriteLine($"Expected height for width=160: {160 / gameMatch.DisplayImageAspectRatio:F1}");
        }

        Console.WriteLine("\n✅ Test completed!");
    }
}
