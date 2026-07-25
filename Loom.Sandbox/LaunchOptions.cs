namespace Loom.Sandbox;

/// <summary>
/// The sandbox's own command-line options — the flags the reusable desktop host does not consume as its own
/// (backend/presentation/dev-shader flags live in <c>HostOptions</c>), delivered here through
/// <c>LoomApplication.AppArgs</c> and parsed in <see cref="SandboxApp"/>'s <c>Configure</c>. Deliberately tiny:
/// <c>--key value</c>, <c>--key=value</c>, and bare <c>--flag</c> (optionally <c>--flag=false</c>). An unrecognized
/// token is warned about and skipped rather than aborting the app — the host has already validated and consumed its
/// own flags (including <c>-h</c>/<c>--help</c>), so what reaches here is meant for the app.
/// </summary>
internal sealed record LaunchOptions
{
	/// <summary>Demo to start on — a 0-based index, an exact name, or a unique case-insensitive substring (e.g.
	/// "bloom"). Null starts on the first demo; an ambiguous or unmatched value is reported and the sandbox exits.</summary>
	public string? Demo { get; init; }

	/// <summary>Prefix for the DOT/JSON graph dump written on startup and every swap; null disables.</summary>
	public string? DumpGraph { get; init; }

	/// <summary>Open the resource viewer on startup.</summary>
	public bool Inspect { get; init; }

	/// <summary>Enable the detached debug camera + frozen-frustum wireframe on startup.</summary>
	public bool Frustum { get; init; }

	/// <summary>Draw the Dear ImGui overlay (menu bar + HUD). On by default; <c>--no-hud</c> suppresses it so only
	/// the demo's own output shows — the apples-to-apples view for the chrome-free WebGPU backend.</summary>
	public bool Hud { get; init; } = true;

	/// <summary>Parse the app's own flags out of <paramref name="args"/> (already stripped of the host's flags).
	/// Malformed or unrecognized tokens are warned to stderr and skipped, never fatal.</summary>
	public static LaunchOptions Parse(string[] args)
	{
		string? demo = null;
		string? dumpGraph = null;
		var inspect = false;
		var frustum = false;
		var hud = true;

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
					case "--demo": demo = Value(); break;
					case "--dump-graph": dumpGraph = Value(); break;
					case "--inspect": inspect = Flag(); break;
					case "--frustum": frustum = Flag(); break;
					case "--no-hud": hud = false; break;
					default: Console.Error.WriteLine($"[loom] ignoring unrecognized sandbox option '{arg}'."); break;
				}
			}
			catch (Exception ex) when (ex is FormatException or ArgumentException)
			{
				Console.Error.WriteLine($"[loom] {ex.Message}");
			}
		}

		return new LaunchOptions { Demo = demo, DumpGraph = dumpGraph, Inspect = inspect, Frustum = frustum, Hud = hud };
	}

	private static bool ParseBool(string value) => value.ToLowerInvariant() switch
	{
		"1" or "true" or "on" or "yes" => true,
		"0" or "false" or "off" or "no" => false,
		_ => throw new ArgumentException($"expected a boolean (on/off), got '{value}'"),
	};
}
