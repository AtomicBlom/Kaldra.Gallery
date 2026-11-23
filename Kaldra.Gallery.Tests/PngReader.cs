using System.Buffers.Binary;
using System.IO.Compression;

namespace BinaryVibrance.Kaldra.Tests;

/// <summary>
/// Minimal, dependency-free PNG decoder (8-bit RGBA/RGB) — the inverse of the backends' <c>PngWriter</c>, for the
/// golden-image harness (decode a capture + its committed baseline, then diff). Handles the standard scanline
/// filters (0–4) so it reads any conforming 8-bit non-interlaced PNG, not just our writer's filter-0 output.
/// </summary>
internal static class PngReader
{
	private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

	public static (int Width, int Height, byte[] Rgba) Read(string path)
	{
		var bytes = File.ReadAllBytes(path);
		if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
			throw new InvalidDataException($"{path} is not a PNG.");

		int width = 0, height = 0, colorType = 0, bitDepth = 0;
		using var idat = new MemoryStream();
		var offset = 8;
		while (offset + 8 <= bytes.Length)
		{
			var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
			var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
			var data = bytes.AsSpan(offset + 8, length);
			switch (type)
			{
				case "IHDR":
					width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
					height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
					bitDepth = data[8];
					colorType = data[9];
					break;
				case "IDAT":
					idat.Write(data);
					break;
			}
			offset += 12 + length; // length + type + data + crc
			if (type == "IEND")
				break;
		}

		if (bitDepth != 8 || colorType is not (6 or 2))
			throw new NotSupportedException($"{path}: only 8-bit RGBA/RGB PNGs are supported (got bitDepth={bitDepth}, colorType={colorType}).");

		var channels = colorType == 6 ? 4 : 3;
		var stride = width * channels;
		idat.Position = 0;
		using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
		var raw = new byte[height * (1 + stride)];
		zlib.ReadExactly(raw);

		// Reverse the per-scanline filter (PNG spec §9). bpp = bytes per pixel.
		var image = new byte[height * stride];
		for (var y = 0; y < height; y++)
		{
			var filter = raw[y * (1 + stride)];
			var src = raw.AsSpan(y * (1 + stride) + 1, stride);
			var dst = image.AsSpan(y * stride, stride);
			var prev = y > 0 ? image.AsSpan((y - 1) * stride, stride) : default;
			for (var x = 0; x < stride; x++)
			{
				int a = x >= channels ? dst[x - channels] : 0;       // left
				int b = prev.Length > 0 ? prev[x] : 0;               // up
				int c = x >= channels && prev.Length > 0 ? prev[x - channels] : 0; // up-left
				dst[x] = (byte)(src[x] + filter switch
				{
					0 => 0,
					1 => a,
					2 => b,
					3 => (a + b) / 2,
					4 => Paeth(a, b, c),
					_ => throw new NotSupportedException($"{path}: unknown PNG filter {filter}."),
				});
			}
		}

		if (channels == 4)
			return (width, height, image);

		// Expand RGB → RGBA (opaque) for a uniform comparison buffer.
		var rgba = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			rgba[i * 4] = image[i * 3];
			rgba[i * 4 + 1] = image[i * 3 + 1];
			rgba[i * 4 + 2] = image[i * 3 + 2];
			rgba[i * 4 + 3] = 255;
		}
		return (width, height, rgba);
	}

	private static int Paeth(int a, int b, int c)
	{
		var p = a + b - c;
		int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
