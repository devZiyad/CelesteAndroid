using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace CelesteAndroid.Steam
{
	/// <summary>Resultado de uma autenticação: o nome da conta e o refresh token (a única coisa que o app guarda, criptografada).</summary>
	public sealed record SteamAuthResult(string AccountName, string RefreshToken);

	/// <summary>Quem recebe o desafio de QR (renova de tempos em tempos) e o aviso de "aprove no app da Steam".</summary>
	public interface ISteamLoginEvents
	{
		void OnQrChallenge(string challengeUrl);
		void OnWaitingForDeviceApproval();
		void OnNeedGuardCode(bool isEmailCode, string? emailHint, bool previousCodeWasIncorrect);
	}

	public sealed class SteamException : Exception
	{
		public SteamException(string message, Exception? inner = null) : base(message, inner) { }
	}

	/// <summary>
	/// Sessão com a Steam (SteamKit2): conexão por WebSocket, login (QR, usuário/senha com aprovação no app, ou refresh token),
	/// verificação de posse do app e download do manifesto/chunks do depot. Nunca registra senha, token ou chave de depot.
	/// </summary>
	public sealed class SteamSession : IDisposable
	{
		public const uint AppId = 504230;
		public const uint LinuxDepotId = 504233;
		/// <summary>Só o ramo público padrão: nunca o beta "opengl" nem outro.</summary>
		public const string Branch = "public";

		private readonly SteamClient client;
		private readonly CallbackManager callbacks;
		private readonly SteamUser user;
		private readonly SteamApps apps;
		private readonly SteamContent content;
		private readonly CancellationTokenSource lifetime = new();
		private readonly Action<string> log;
		private TaskCompletionSource<bool> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private TaskCompletionSource<SteamUser.LoggedOnCallback> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private SteamApps.LicenseListCallback? licenses;
		private TaskCompletionSource<SteamApps.LicenseListCallback> licenseList = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Task? pump;
		private bool disposed;

		public bool IsLoggedOn { get; private set; }
		public bool IsConnected { get; private set; }

		private sealed class DiagnosticListener : IDebugListener
		{
			private readonly Action<string> sink;
			public DiagnosticListener(Action<string> sink) => this.sink = sink;

			public void WriteLine(string category, string msg)
			{
				// Só em builds de depuração e sem nada que lembre segredo.
				if (msg.Contains("token", StringComparison.OrdinalIgnoreCase) || msg.Contains("password", StringComparison.OrdinalIgnoreCase)
					|| msg.Contains("refresh", StringComparison.OrdinalIgnoreCase) || msg.Contains("access", StringComparison.OrdinalIgnoreCase))
					return;
				sink($"steamkit[{category}]: {msg}");
			}
		}

		public SteamSession(Action<string> log)
		{
			this.log = log;
#if DEBUG
			DebugLog.AddListener(new DiagnosticListener(log));
			DebugLog.Enabled = true;
#endif
			SteamConfiguration config = SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp));
			client = new SteamClient(config);
			callbacks = new CallbackManager(client);
			user = client.GetHandler<SteamUser>()!;
			apps = client.GetHandler<SteamApps>()!;
			content = client.GetHandler<SteamContent>()!;
			callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => { IsConnected = true; connected.TrySetResult(true); });
			callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
			callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn.TrySetResult(c));
			callbacks.Subscribe<SteamApps.LicenseListCallback>(c => { licenses = c; licenseList.TrySetResult(c); });
		}

		private void OnDisconnected(SteamClient.DisconnectedCallback cb)
		{
			IsLoggedOn = false;
			IsConnected = false;
			log($"steam: disconnected (user initiated: {cb.UserInitiated})");
			if (cb.UserInitiated)
				return; // um Disconnect() nosso (troca de servidor) não derruba a tentativa seguinte
			connected.TrySetException(new SteamException("Disconnected from Steam."));
			loggedOn.TrySetException(new SteamException("Disconnected from Steam."));
		}

		#region Conexão

		public async Task ConnectAsync(CancellationToken ct)
		{
			pump ??= Task.Run(async () =>
			{
				while (!lifetime.IsCancellationRequested)
				{
					try { await callbacks.RunWaitCallbackAsync(lifetime.Token).ConfigureAwait(false); }
					catch (OperationCanceledException) { break; }
				}
			});
			// Cada Connect() usa o próximo servidor da lista: se um não responde (porta bloqueada, servidor fora do ar), tenta o seguinte.
			const int attempts = 8;
			for (int attempt = 1; ; attempt++)
			{
				connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
				log($"steam: connecting (attempt {attempt}/{attempts})");
				client.Connect();
				try
				{
					await WaitAsync(connected.Task, TimeSpan.FromSeconds(20), ct, "connecting to Steam").ConfigureAwait(false);
					log("steam: connected");
					return;
				}
				catch (SteamException) when (attempt < attempts)
				{
					try { client.Disconnect(); } catch (Exception) { }
					await Task.Delay(400, ct).ConfigureAwait(false);
				}
			}
		}

		private static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct, string what)
		{
			Task finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			if (finished != task)
				throw new SteamException($"Timed out {what}.");
			return await task.ConfigureAwait(false);
		}

		#endregion

		#region Login

		private static AuthSessionDetails Details() => new()
		{
			DeviceFriendlyName = "Celeste (Android)",
			IsPersistentSession = true,
		};

		/// <summary>
		/// Login por QR: <paramref name="events"/> recebe a URL do desafio (a que o botão "Abrir no app da Steam" abre e que vira o
		/// QR code); a aprovação no app da Steam do mesmo celular (ou de outro aparelho) conclui o login.
		/// </summary>
		public async Task<SteamAuthResult> LoginWithQrAsync(ISteamLoginEvents events, CancellationToken ct)
		{
			QrAuthSession session = await client.Authentication.BeginAuthSessionViaQRAsync(Details()).ConfigureAwait(false);
			session.ChallengeURLChanged = () => events.OnQrChallenge(session.ChallengeURL); // a Steam renova a URL de tempos em tempos
			events.OnQrChallenge(session.ChallengeURL);
			log("steam: QR challenge issued, waiting for approval in the Steam app");
			AuthPollResult result = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
			log("steam: QR sign-in approved");
			return new SteamAuthResult(result.AccountName, result.RefreshToken);
		}

		private CredentialsAuthSession? credentialsSession;

		/// <summary>
		/// Login por usuário/senha: depois da senha espera a aprovação por push no app da Steam; se o usuário preferir, um código
		/// do Steam Guard digitado vai por <see cref="SubmitGuardCodeAsync"/> (último recurso). A senha só vive em memória, nesta chamada.
		/// </summary>
		public async Task<SteamAuthResult> LoginWithCredentialsAsync(string username, string password, ISteamLoginEvents events, CancellationToken ct)
		{
			AuthSessionDetails details = Details();
			details.Username = username.Trim();
			details.Password = password;
			details.Authenticator = new PushFirstAuthenticator(events);
			CredentialsAuthSession session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(details).ConfigureAwait(false);
			details.Password = null;
			credentialsSession = session;
			log("steam: credentials accepted, waiting for approval");
			try
			{
				AuthPollResult result = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
				log("steam: credentials sign-in approved");
				return new SteamAuthResult(result.AccountName, result.RefreshToken);
			}
			finally
			{
				credentialsSession = null;
			}
		}

		/// <summary>Código do app autenticador (ou do e-mail) digitado enquanto o login espera a aprovação.</summary>
		public async Task SubmitGuardCodeAsync(string code, bool isEmailCode)
		{
			CredentialsAuthSession session = credentialsSession ?? throw new SteamException("No sign-in is waiting for a code.");
			await session.SendSteamGuardCodeAsync(code.Trim(), isEmailCode ? EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode : EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode).ConfigureAwait(false);
		}

		private sealed class PushFirstAuthenticator : IAuthenticator
		{
			private readonly ISteamLoginEvents events;
			public PushFirstAuthenticator(ISteamLoginEvents events) => this.events = events;

			public Task<bool> AcceptDeviceConfirmationAsync()
			{
				events.OnWaitingForDeviceApproval();
				return Task.FromResult(true); // espera o push no app da Steam; o código digitado continua disponível
			}

			public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
			{
				events.OnNeedGuardCode(false, null, previousCodeWasIncorrect);
				return new TaskCompletionSource<string>().Task; // o código chega por SubmitGuardCodeAsync
			}

			public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
			{
				events.OnNeedGuardCode(true, email, previousCodeWasIncorrect);
				return new TaskCompletionSource<string>().Task;
			}
		}

		/// <summary>Entra na Steam com o refresh token guardado (sem senha, sem interação).</summary>
		public async Task LogOnAsync(string accountName, string refreshToken, CancellationToken ct)
		{
			loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
			licenseList = new(TaskCreationOptions.RunContinuationsAsynchronously);
			user.LogOn(new SteamUser.LogOnDetails
			{
				Username = accountName,
				AccessToken = refreshToken,
				ShouldRememberPassword = true,
			});
			SteamUser.LoggedOnCallback result = await WaitAsync(loggedOn.Task, TimeSpan.FromSeconds(30), ct, "logging on").ConfigureAwait(false);
			if (result.Result != EResult.OK)
				throw new SteamException($"Steam refused the sign-in: {result.Result}.");
			IsLoggedOn = true;
			log("steam: logged on");
			await WaitAsync(licenseList.Task, TimeSpan.FromSeconds(30), ct, "receiving the license list").ConfigureAwait(false);
		}

		#endregion

		#region Posse e manifesto

		/// <summary>Confirma que a conta tem uma licença (própria ou de compartilhamento familiar) que inclui o app 504230.</summary>
		public async Task<bool> OwnsGameAsync(CancellationToken ct)
		{
			SteamApps.LicenseListCallback list = licenses ?? throw new SteamException("Not logged on.");
			var requests = list.LicenseList.Select(l => new SteamApps.PICSRequest(l.PackageID, l.AccessToken)).ToList();
			if (requests.Count == 0)
				return false;
			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await apps.PICSGetProductInfo(Array.Empty<SteamApps.PICSRequest>(), requests).ToTask().ConfigureAwait(false);
			foreach (SteamApps.PICSProductInfoCallback part in info.Results ?? Enumerable.Empty<SteamApps.PICSProductInfoCallback>())
			{
				foreach (var package in part.Packages.Values)
				{
					KeyValue appids = package.KeyValues["appids"];
					if (appids.Children.Any(c => c.AsUnsignedInteger() == AppId))
						return true;
				}
			}
			return false;
		}

		private byte[]? depotKey;

		/// <summary>
		/// Descobre o manifesto do depot Linux no ramo público (recusa qualquer outro depot/ramo), baixa-o e o devolve como
		/// <see cref="ManifestSnapshot"/>, junto com a fonte de chunks para o <see cref="DepotInstaller"/>.
		/// </summary>
		public async Task<(ManifestSnapshot Manifest, IChunkSource Chunks)> GetManifestAsync(CancellationToken ct)
		{
			// Chave do depot: também prova a posse (AccessDenied se a conta não tem o jogo).
			SteamApps.DepotKeyCallback key = await apps.GetDepotDecryptionKey(LinuxDepotId, AppId).ToTask().ConfigureAwait(false);
			if (key.Result != EResult.OK)
				throw new SteamException($"Steam did not grant the depot key for {LinuxDepotId}: {key.Result} (does this account own Celeste?).");
			depotKey = key.DepotKey;

			SteamApps.PICSTokensCallback tokens = await apps.PICSGetAccessTokens(new[] { AppId }, Array.Empty<uint>()).ToTask().ConfigureAwait(false);
			tokens.AppTokens.TryGetValue(AppId, out ulong appToken);
			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(AppId, appToken), null).ToTask().ConfigureAwait(false);
			KeyValue appInfo = info.Results!.SelectMany(r => r.Apps.Values).Single(a => a.ID == AppId).KeyValues;

			KeyValue depot = appInfo["depots"][LinuxDepotId.ToString()];
			if (depot == KeyValue.Invalid)
				throw new SteamException($"Depot {LinuxDepotId} is not part of app {AppId}.");
			string oslist = depot["config"]["oslist"].AsString() ?? "";
			if (oslist.Length > 0 && !oslist.Contains("linux", StringComparison.OrdinalIgnoreCase))
				throw new SteamException($"Depot {LinuxDepotId} is not the Linux depot (oslist '{oslist}').");

			KeyValue branchNode = depot["manifests"][Branch];
			string? gid = branchNode["gid"].AsString() ?? branchNode.AsString();
			if (!ulong.TryParse(gid, out ulong manifestId) || manifestId == 0)
				throw new SteamException($"No manifest for depot {LinuxDepotId} on the '{Branch}' branch.");
			log($"steam: depot {LinuxDepotId} branch {Branch} manifest {manifestId}");

			var servers = await GetCdnServersAsync().ConfigureAwait(false);
			var cdn = new SteamKit2.CDN.Client(client);
			ulong requestCode = await content.GetManifestRequestCode(LinuxDepotId, AppId, manifestId, Branch).ConfigureAwait(false);
			DepotManifest manifest = await cdn.DownloadManifestAsync(LinuxDepotId, manifestId, requestCode, servers[0], depotKey, null, null).ConfigureAwait(false);

			var chunkData = new Dictionary<string, DepotManifest.ChunkData>(StringComparer.Ordinal);
			var files = new List<ManifestFile>();
			foreach (DepotManifest.FileData f in manifest.Files ?? new List<DepotManifest.FileData>())
			{
				var chunks = new List<ManifestChunk>();
				foreach (DepotManifest.ChunkData c in f.Chunks)
				{
					string id = Convert.ToHexString(c.ChunkID!).ToLowerInvariant();
					chunkData[id] = c;
					chunks.Add(new ManifestChunk(id, (long)c.Offset, (int)c.UncompressedLength, (int)c.CompressedLength, c.Checksum));
				}
				files.Add(new ManifestFile(ManifestFile.NormalizePath(f.FileName), (long)f.TotalSize, (int)f.Flags,
					f.FileHash is { Length: > 0 } ? Convert.ToHexString(f.FileHash).ToLowerInvariant() : "", chunks));
			}
			var snapshot = new ManifestSnapshot(AppId, LinuxDepotId, manifestId, Branch, files);
			return (snapshot, new SteamChunkSource(this, cdn, servers, chunkData));
		}

		private async Task<List<Server>> GetCdnServersAsync()
		{
			IReadOnlyCollection<Server> all = await content.GetServersForSteamPipe().ConfigureAwait(false);
			var servers = all
				.Where(s => (s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(AppId)) && (s.Type == "SteamCache" || s.Type == "CDN"))
				.OrderBy(s => s.WeightedLoad).ToList();
			if (servers.Count == 0)
				throw new SteamException("Steam returned no download servers.");
			log($"steam: {servers.Count} download servers");
			return servers;
		}

		/// <summary>Baixa chunks do CDN, trocando de servidor e pedindo token de CDN quando necessário.</summary>
		private sealed class SteamChunkSource : IChunkSource
		{
			private readonly SteamSession session;
			private readonly SteamKit2.CDN.Client cdn;
			private readonly List<Server> servers;
			private readonly Dictionary<string, DepotManifest.ChunkData> chunks;
			private readonly ConcurrentDictionary<string, string> cdnTokens = new();
			private int next;

			public SteamChunkSource(SteamSession session, SteamKit2.CDN.Client cdn, List<Server> servers, Dictionary<string, DepotManifest.ChunkData> chunks)
			{
				this.session = session; this.cdn = cdn; this.servers = servers; this.chunks = chunks;
			}

			public async Task<int> DownloadChunkAsync(ManifestChunk chunk, byte[] buffer, CancellationToken ct)
			{
				DepotManifest.ChunkData data = chunks[chunk.Id];
				Exception? last = null;
				for (int attempt = 0; attempt < servers.Count * 2 && attempt < 8; attempt++)
				{
					ct.ThrowIfCancellationRequested();
					Server server = servers[(Interlocked.Increment(ref next) - 1) % servers.Count];
					cdnTokens.TryGetValue(server.Host!, out string? token);
					try
					{
						return await cdn.DownloadDepotChunkAsync(LinuxDepotId, data, server, buffer, session.depotKey, null, token).ConfigureAwait(false);
					}
					catch (SteamKitWebRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Forbidden && token == null)
					{
						SteamContent.CDNAuthToken auth = await session.content.GetCDNAuthToken(AppId, LinuxDepotId, server.Host!).ConfigureAwait(false);
						if (auth.Result == EResult.OK && auth.Token != null)
							cdnTokens[server.Host!] = auth.Token;
						last = e;
					}
					catch (Exception e) when (e is not OperationCanceledException)
					{
						last = e;
					}
				}
				throw new SteamException("Could not download a chunk from any Steam content server.", last);
			}
		}

		#endregion

		public void Logout()
		{
			try { if (IsLoggedOn) user.LogOff(); } catch (Exception) { }
			IsLoggedOn = false;
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			Logout();
			try { client.Disconnect(); } catch (Exception) { }
			lifetime.Cancel();
		}
	}
}
