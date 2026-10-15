using Microsoft.Extensions.Logging;
using Broadsheet.Telemetry;
using Telerik.Blazor.Services;

namespace Broadsheet.Ui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
#if MACCATALYST
		EnsureHomebrewOnPath();
#endif
		// No fonts are registered: the only MAUI control is the BlazorWebView, which takes its
		// typography from wwwroot/app.css.
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddTelerikBlazor();

		// DefaultAzureCredential picks up the developer's `az login`.
		builder.Services.AddSingleton<BroadsheetTelemetryService>();
		builder.Services.AddSingleton(BroadsheetOptions.Load());

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

#if MACCATALYST
	// GUI-launched apps inherit a minimal PATH (/usr/bin:/bin:...) that omits Homebrew, so the
	// `az` CLI that AzureCliCredential shells out to isn't found. Prepend the usual install dirs.
	private static void EnsureHomebrewOnPath()
	{
		var parts = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(':', StringSplitOptions.RemoveEmptyEntries)
			.ToList();

		foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
		{
			if (Directory.Exists(dir) && !parts.Contains(dir))
			{
				parts.Add(dir);
			}
		}

		Environment.SetEnvironmentVariable("PATH", string.Join(':', parts));
	}
#endif
}
