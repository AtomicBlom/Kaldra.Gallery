namespace Loom.Sandbox;

/// <summary>
/// The sandbox's command-line configuration — the single replacement for the pile of <c>LOOM_*</c> environment
/// variables earlier milestones used to steer the launcher. Everything that used to be an env var is now an
/// explicit option (run <c>dotnet run --project Loom.Sandbox -- --help</c>), so a run is self-describing and a
/// verification script passes flags instead of mutating process state. (The engine assemblies read no environment
/// variables at all; the only remaining one is <c>LOOM_GOLDEN_DIR</c>, which configures <c>dotnet test</c>, not
/// this launcher. The <c>LOOM_IMGUI</c>/<c>LOOM_DX12</c>/… symbols are compile-time build flags, not runtime vars.)
/// <para>
/// Parsing is deliberately tiny and dependency-free: <c>--key value</c>, <c>--key=value</c>, and bare
/// <c>--flag</c> (optionally <c>--flag=false</c>). An unknown or malformed option prints usage and sets
/// <see cref="ShowHelp"/> so the caller exits cleanly.
/// </para>
/// </summary>
internal sealed record LaunchOptions
{
	/// <summary>Backend to run on, or null to auto-select. Maps to the right window surface + <c>EngineSettings</c>
	/// subclass. Null lets the host pick the first backend compiled into this build (Vulkan when present).</summary>
	public BackendChoice? Backend { get; init; }

	/// <summary>Present with v-sync (default on); <c>--no-vsync</c> or <c>--vsync off</c> selects the low-latency mode.</summary>
	public bool VSync { get; init; } = true;

	/// <summary>Frame-rate cap for the uncapped (no-vsync) loop; 0 = unlimited.</summary>
	public double MaxFps { get; init; }

	/// <summary>Request true HDR (scRGB) display output.</summary>
	public bool Hdr { get; init; }

	/// <summary>Watch the loose shader directory and hot-reload rebuilt artifacts (dev only).</summary>
	public bool WatchShaders { get; init; }

	/// <summary>Demo to start on — a 0-based index, an exact name, or a unique case-insensitive substring (e.g.
	/// "bloom"). Null starts on the first demo; an ambiguous or unmatched value is reported and the sandbox exits.</summary>
	public string? Demo { get; init; }

	/// <summary>Prefix for the DOT/JSON graph dump written on startup and every swap; null disables.</summary>
	public string? DumpGraph { get; init; }

	/// <summary>Open the M13 resource viewer on startup (handy for headless capture verification).</summary>
	public bool Inspect { get; init; }

	/// <summary>Enable the M13 detached debug camera + frozen-frustum wireframe on startup.</summary>
	public bool Frustum { get; init; }

	/// <summary>Headless: render N frames (cycling demos) then exit. 0 = run interactively.</summary>
	public int SmokeFrames { get; init; }

	/// <summary>Capture one frame to this PNG path with deterministic time, then exit. Null disables.</summary>
	public string? Capture { get; init; }

	/// <summary>
	/// With <see cref="Capture"/>: advance time deterministically (time = frame·delta) instead of freezing it.
	/// The captured frame lands on the same value as the frozen path, so a correct backend's capture is still
	/// byte-identical — but the frames before it now differ, surfacing cross-frame hazards a frozen capture hides.
	/// </summary>
	public bool CaptureAnimate { get; init; }

	/// <summary>Print usage and exit. Set by <c>--help</c> or by any parse error.</summary>
	public bool ShowHelp { get; init; }

	public static string Usage =>
		"""
		Loom sandbox — per-capability demo gallery.

		Usage: dotnet run --project Loom.Sandbox -- [options]

		Backend:
		  --backend <vulkan|dx12>   Backend to run on (default: vulkan).

		Presentation:
		  --no-vsync                Use the low-latency present mode (v-sync is on by default).
		  --vsync <on|off>          Explicit form of the above.
		  --max-fps <n>             Frame-rate cap for the no-vsync loop (0 = unlimited).
		  --hdr                     Request true HDR (scRGB) display output.

		Demo selection:
		  --demo <name|index>       Start on this demo: a 0-based index, an exact name, or a unique
		                            case-insensitive substring (an ambiguous substring lists its matches).

		Dev / debug:
		  --watch-shaders           Hot-reload rebuilt shader artifacts.
		  --dump-graph <prefix>     Write "<prefix>.<demo>.dot/.json" on startup and every swap.
		  --inspect                 Open the resource viewer on startup.
		  --frustum                 Enable the detached debug camera + frozen-frustum wireframe.

		Headless / capture:
		  --smoke-frames <n>        Render N frames (cycling demos) then exit.
		  --capture <path>          Capture one frame to a PNG (deterministic time) then exit.
		  --capture-animate         With --capture: advance time instead of freezing (surfaces cross-frame hazards).

		  -h, --help                Show this help.
		""";

	/// <summary>Parse <paramref name="args"/> into options. On an unknown/malformed option, writes the reason to
	/// stderr and returns a value with <see cref="ShowHelp"/> set so the caller can print usage and exit.</summary>
	public static LaunchOptions Parse(string[] args)
	{
		BackendChoice? backend = null;
		var vsync = true;
		var maxFps = 0.0;
		var hdr = false;
		var watch = false;
		string? demo = null;
		string? dumpGraph = null;
		var inspect = false;
		var frustum = false;
		var smokeFrames = 0;
		string? capture = null;
		var captureAnimate = false;
		var help = false;

		LaunchOptions Fail(string message)
		{
			Console.Error.WriteLine($"[loom] {message}");
			return new LaunchOptions { ShowHelp = true };
		}

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			var key = arg;
			string? inlineValue = null;
			if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq && eq > 0)
			{
				key = arg[..eq];
				inlineValue = arg[(eq + 1)..];
			}

			// A value option reads its inline "=value" or the next token; throws (caught below) if neither exists.
			string Value()
			{
				if (inlineValue is not null)
					return inlineValue;
				if (i + 1 < args.Length)
					return args[++i];
				throw new FormatException($"option '{key}' requires a value");
			}

			// A flag is true when bare; "--flag=false" / "--flag=off" can still turn it off.
			bool Flag() => inlineValue is null || ParseBool(inlineValue);

			try
			{
				switch (key.ToLowerInvariant())
				{
					case "--backend": backend = ParseBackend(Value()); break;
					case "--vsync": vsync = ParseBool(Value()); break;
					case "--no-vsync": vsync = false; break;
					case "--max-fps":
						maxFps = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture);
						if (maxFps < 0) return Fail("--max-fps must be non-negative.");
						break;
					case "--hdr": hdr = Flag(); break;
					case "--watch-shaders": watch = Flag(); break;
					case "--demo": demo = Value(); break;
					case "--dump-graph": dumpGraph = Value(); break;
					case "--inspect": inspect = Flag(); break;
					case "--frustum": frustum = Flag(); break;
					case "--smoke-frames":
						smokeFrames = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture);
						if (smokeFrames <= 0) return Fail("--smoke-frames must be a positive frame count (a headless run needs N > 0 frames to render then exit).");
						break;
					case "--capture": capture = Value(); break;
					case "--capture-animate": captureAnimate = Flag(); break;
					case "--help" or "-h" or "-?": help = true; break;
					default: return Fail($"unknown option '{arg}'");
				}
			}
			catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
			{
				return Fail(ex.Message);
			}
		}

		return new LaunchOptions
		{
			Backend = backend,
			VSync = vsync,
			MaxFps = maxFps,
			Hdr = hdr,
			WatchShaders = watch,
			Demo = demo,
			DumpGraph = dumpGraph,
			Inspect = inspect,
			Frustum = frustum,
			SmokeFrames = smokeFrames,
			Capture = capture,
			CaptureAnimate = captureAnimate,
			ShowHelp = help,
		};
	}

	private static BackendChoice ParseBackend(string value) => value.ToLowerInvariant() switch
	{
		"vulkan" or "vk" => BackendChoice.Vulkan,
		"dx12" or "d3d12" or "directx12" or "directx" => BackendChoice.Dx12,
		_ => throw new ArgumentException($"unknown backend '{value}' (expected vulkan or dx12)"),
	};

	private static bool ParseBool(string value) => value.ToLowerInvariant() switch
	{
		"1" or "true" or "on" or "yes" => true,
		"0" or "false" or "off" or "no" => false,
		_ => throw new ArgumentException($"expected a boolean (on/off), got '{value}'"),
	};
}
