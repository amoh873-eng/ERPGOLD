using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace ERPSystem.Mobile;

/// <summary>نقطة الإقلاع الرسمية التي يكتشفها SDK (كقالب maui الرسمي).</summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts => { });

        return builder.Build();
    }
}