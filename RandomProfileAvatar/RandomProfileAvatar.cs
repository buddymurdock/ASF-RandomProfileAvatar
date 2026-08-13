using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomProfileAvatar;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary image/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomProfileAvatar : IASF, IBotConnection, IGitHubPluginUpdates {
	private const string BundledAvatarsFileName = "avatars.json";
	private const ushort DefaultMaxDelayInDays = 60;
	private const ushort DefaultMinDelayInDays = 14;

	private static readonly Uri SteamCommunityURL = new("https://steamcommunity.com");
	private static readonly Uri AvatarUploadURL = new(SteamCommunityURL, "/actions/FileUploader");

	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);

	// Last avatar URL successfully set per bot, purely so we don't roll the same image twice in a row
	private readonly ConcurrentDictionary<string, string> BotLastAvatarURL = new(StringComparer.Ordinal);

	private string[] AvatarPool = [];
	private volatile bool EmptyPoolWarningLogged;
	private bool Enabled;
	private ushort MaxDelayInDays = DefaultMaxDelayInDays;
	private ushort MinDelayInDays = DefaultMinDelayInDays;
	private bool UseBundledAvatars;

	public string Name => nameof(RandomProfileAvatar);
	public string RepositoryName => "buddymurdock/ASF-RandomProfileAvatar";
	public Version Version => typeof(RandomProfileAvatar).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomProfileAvatarEnabled / RandomProfileAvatarMinDelayDays / RandomProfileAvatarMaxDelayDays /
	// RandomProfileAvatarAvatarURLs / RandomProfileAvatarUseBundledAvatars from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		HashSet<string> parsedAvatarURLs = [];

		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomProfileAvatar)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomProfileAvatar)}MinDelayDays" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInDays = minDelay;

						break;
					case $"{nameof(RandomProfileAvatar)}MaxDelayDays" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInDays = maxDelay;

						break;
					case $"{nameof(RandomProfileAvatar)}UseBundledAvatars" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						UseBundledAvatars = configValue.GetBoolean();

						break;
					case $"{nameof(RandomProfileAvatar)}AvatarURLs" when configValue.ValueKind == JsonValueKind.Array:
						AddParsedAvatarURLs(configValue, parsedAvatarURLs);

						break;
				}
			}
		}

		if (UseBundledAvatars) {
			LoadBundledAvatarURLs(parsedAvatarURLs);
		}

		AvatarPool = [.. parsedAvatarURLs];

		if (MinDelayInDays > MaxDelayInDays) {
			(MinDelayInDays, MaxDelayInDays) = (MaxDelayInDays, MinDelayInDays);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomProfileAvatar)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInDays}-{MaxDelayInDays} days each bot randomly changes its avatar, picking from a pool of {AvatarPool.Length} image(s).");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotAvatarLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	private async Task BotAvatarLoopAsync(Bot bot, CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			int delayDays = MinDelayInDays == MaxDelayInDays ? MinDelayInDays : Random.Shared.Next(MinDelayInDays, MaxDelayInDays + 1);

			try {
				await LongDelayAsync(TimeSpan.FromDays(delayDays), cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				await TrySetRandomAvatarAsync(bot).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a single
	// Task.Delay(TimeSpan.FromDays(60)) throws ArgumentOutOfRangeException immediately, which
	// went unhandled here and crashed the entire ASF process via OnUnobservedTaskException.
	// Chunking sidesteps the limit for arbitrarily long delays.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task TrySetRandomAvatarAsync(Bot bot) {
		if (AvatarPool.Length == 0) {
			if (!EmptyPoolWarningLogged) {
				EmptyPoolWarningLogged = true;

				ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomProfileAvatar)}AvatarURLs is empty, set it or {nameof(RandomProfileAvatar)}UseBundledAvatars in ASF.json for this plugin to do anything.");
			}

			return;
		}

		BotLastAvatarURL.TryGetValue(bot.BotName, out string? lastAvatarURL);

		string[] candidates = (AvatarPool.Length > 1) && (lastAvatarURL != null) ? [.. AvatarPool.Where(url => url != lastAvatarURL)] : AvatarPool;

		string avatarURL = candidates[Random.Shared.Next(candidates.Length)];

		if (!TryGetImageFormat(avatarURL, out string? fileName, out string? contentType)) {
			bot.ArchiLogger.LogGenericWarning($"Skipping avatar candidate with an unrecognized image format: {avatarURL}.");

			return;
		}

		BinaryResponse? imageResponse = await bot.ArchiWebHandler.WebBrowser.UrlGetToBinary(new Uri(avatarURL)).ConfigureAwait(false);

		if (imageResponse?.Content is not { Count: > 0 } imageBytes) {
			bot.ArchiLogger.LogGenericWarning($"Failed to download avatar candidate: {avatarURL}.");

			return;
		}

		bool success = await UploadAvatarAsync(bot, [.. imageBytes], fileName, contentType).ConfigureAwait(false);

		if (success) {
			BotLastAvatarURL[bot.BotName] = avatarURL;

			bot.ArchiLogger.LogGenericInfo($"Randomly changed avatar to {avatarURL}.");
		} else {
			bot.ArchiLogger.LogGenericWarning($"Failed to set avatar to {avatarURL}.");
		}
	}

	private static async Task<bool> UploadAvatarAsync(Bot bot, byte[] imageBytes, string fileName, string contentType) {
		string? sessionID = bot.ArchiWebHandler.WebBrowser.CookieContainer.GetCookieValue(SteamCommunityURL, "sessionid");

		if (string.IsNullOrEmpty(sessionID)) {
			return false;
		}

		// MultipartFormDataContent.Dispose() disposes every part added to it via Add(), so the outer `using` below covers all of these despite the analyzer not tracking that ownership transfer
#pragma warning disable CA2000
		using MultipartFormDataContent content = new() {
			{ new StringContent(imageBytes.Length.ToString(CultureInfo.InvariantCulture)), "MAX_FILE_SIZE" },
			{ new StringContent("player_avatar_image"), "type" },
			{ new StringContent(bot.SteamID.ToString(CultureInfo.InvariantCulture)), "sId" },
			{ new StringContent(sessionID), "sessionid" },
			{ new StringContent("1"), "doSub" },
			{ new StringContent("1"), "json" }
		};

		ByteArrayContent avatarContent = new(imageBytes);
#pragma warning restore CA2000
		avatarContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		content.Add(avatarContent, "avatar", fileName);

		ObjectResponse<AvatarUploadResponse>? response = await bot.ArchiWebHandler.WebBrowser.UrlPostToJsonObject<AvatarUploadResponse, MultipartFormDataContent>(AvatarUploadURL, data: content, referer: SteamCommunityURL).ConfigureAwait(false);

		return response?.Content?.Success ?? false;
	}

	private static bool TryGetImageFormat(string url, [NotNullWhen(true)] out string? fileName, [NotNullWhen(true)] out string? contentType) {
		string extension = Path.GetExtension(url).TrimStart('.').ToUpperInvariant();

		(fileName, contentType) = extension switch {
			"JPG" or "JPEG" => ("avatar.jpg", "image/jpeg"),
			"PNG" => ("avatar.png", "image/png"),
			"GIF" => ("avatar.gif", "image/gif"),
			_ => (null, null)
		};

		return fileName != null;
	}

	private static void AddParsedAvatarURLs(JsonElement array, HashSet<string> target) {
		foreach (JsonElement urlElement in array.EnumerateArray()) {
			if ((urlElement.ValueKind == JsonValueKind.String) && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri) && ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps))) {
				target.Add(uri.AbsoluteUri);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomProfileAvatar)}AvatarURLs entry: {urlElement}.");
			}
		}
	}

	// Loads avatars.json shipped alongside the plugin DLL, adding its entries on top of whatever came from ASF.json
	private static void LoadBundledAvatarURLs(HashSet<string> target) {
		string? pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		if (string.IsNullOrEmpty(pluginDirectory)) {
			ASF.ArchiLogger.LogGenericWarning($"Could not determine plugin directory, {nameof(RandomProfileAvatar)}UseBundledAvatars will have no effect.");

			return;
		}

		string filePath = Path.Combine(pluginDirectory, BundledAvatarsFileName);

		if (!File.Exists(filePath)) {
			ASF.ArchiLogger.LogGenericWarning($"{BundledAvatarsFileName} not found next to the plugin, {nameof(RandomProfileAvatar)}UseBundledAvatars will have no effect.");

			return;
		}

		List<string>? entries;

		try {
			entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath));
		} catch (JsonException e) {
			ASF.ArchiLogger.LogGenericException(e);

			return;
		}

		if (entries == null) {
			return;
		}

		foreach (string entry in entries) {
			if (Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri) && ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps))) {
				target.Add(uri.AbsoluteUri);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid entry in {BundledAvatarsFileName}: {entry}.");
			}
		}
	}

	private sealed record AvatarUploadResponse([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("message")] string? Message);
}
#pragma warning restore CA5394
#pragma warning restore CA1001
#pragma warning restore CA1812
