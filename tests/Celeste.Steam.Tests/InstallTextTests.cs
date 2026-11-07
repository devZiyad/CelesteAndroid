using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public class InstallTextTests
	{
		[Fact]
		public void Stats_line_matches_the_spec_example() =>
			Assert.Equal("312 / 1217 files · 640 MB / 1.1 GB · 3.0 MB/s",
				InstallText.FormatStats(new TransferStats(312, 1217, 640L << 20, (long)(1.1 * (1L << 30)), 3.0 * (1 << 20))));

		[Fact]
		public void Stats_line_leaves_out_an_unknown_speed() =>
			Assert.Equal("0 / 5 files · 0 MB / 12 MB", InstallText.FormatStats(new TransferStats(0, 5, 0, 12L << 20, 0)));

		[Theory]
		[InlineData(0L, "0 MB")]
		[InlineData(1048576L, "1 MB")]
		[InlineData(1073741823L, "1024 MB")]
		[InlineData(1073741824L, "1.0 GB")]
		[InlineData(1181116006L, "1.1 GB")]
		public void Bytes(long bytes, string expected) => Assert.Equal(expected, InstallText.FormatBytes(bytes));

		[Theory]
		[InlineData(3145728.0, "3.0 MB/s")]
		[InlineData(1048576.0, "1.0 MB/s")]
		[InlineData(524288.0, "512 KB/s")]
		public void Speed(double bytesPerSecond, string expected) => Assert.Equal(expected, InstallText.FormatSpeed(bytesPerSecond));

		[Fact]
		public void Step_line_names_the_file() =>
			Assert.Equal("Downloading Content/Maps/1-ForsakenCity.bin", InstallText.StepLine("Downloading", "Content/Maps/1-ForsakenCity.bin"));

		[Fact]
		public void Step_line_without_a_file() => Assert.Equal("Verifying files", InstallText.StepLine("Verifying", null));

		[Theory]
		[InlineData("short.bin", 20, "short.bin")]
		[InlineData("exactly-ten", 11, "exactly-ten")]
		[InlineData("Content/Maps/1-ForsakenCity.bin", 11, "Conte…y.bin")]
		[InlineData("abcdefghij", 6, "abc…ij")]
		[InlineData("abcdefghij", 2, "a…")]
		[InlineData("abcdefghij", 1, "…")]
		[InlineData("abcdefghij", 0, "")]
		public void Ellipsis_in_the_middle(string text, int max, string expected)
		{
			string result = InstallText.EllipsizeMiddle(text, max);
			Assert.Equal(expected, result);
			Assert.True(result.Length <= max);
		}

		[Fact]
		public void Ellipsis_keeps_both_ends()
		{
			string result = InstallText.EllipsizeMiddle("Content/Maps/1-ForsakenCity.bin", 16);
			Assert.Equal(16, result.Length);
			Assert.StartsWith("Content/", result);
			Assert.EndsWith("ity.bin", result);
			Assert.Contains("…", result);
			Assert.DoesNotContain("...", result);
		}

		[Fact]
		public void Fit_returns_text_that_already_fits() =>
			Assert.Equal("Downloading a.bin", InstallText.FitMiddle("Downloading a.bin", s => s.Length * 10, 500));

		[Theory]
		[InlineData(200)]
		[InlineData(150)]
		[InlineData(100)]
		public void Fit_never_exceeds_the_width_and_keeps_the_prefix(int width)
		{
			const string line = "Downloading Content/Graphics/Atlases/Gameplay/a-very-long-folder-name/and-a-file.data";
			string fitted = InstallText.FitMiddle(line, s => s.Length * 5, width, protectedPrefix: "Downloading ".Length);
			Assert.True(fitted.Length * 5 <= width);
			Assert.StartsWith("Downloading ", fitted);
			Assert.Contains("…", fitted);
			Assert.EndsWith("ta", fitted);
		}

		[Fact]
		public void Fit_is_maximal()
		{
			const string line = "Downloading Content/Graphics/Atlases/Gameplay/a-very-long-folder-name/and-a-file.data";
			string fitted = InstallText.FitMiddle(line, s => s.Length, 40, protectedPrefix: 12);
			Assert.Equal(40, fitted.Length);
		}

		[Fact]
		public void Fit_with_no_room_gives_just_the_ellipsis()
		{
			string fitted = InstallText.FitMiddle("Downloading abcdefghijkl", s => s.Length, 13, protectedPrefix: 12);
			Assert.Equal("Downloading …", fitted);
		}
	}
}
