using CelesteAndroid.Steam;
using Xunit;

namespace CelesteAndroid.Steam.Tests
{
	public class LogScrubberTests
	{
		[Theory]
		[InlineData("I CelesteAndroid: steam: refresh token revoked")]
		[InlineData("D Steam: Password=hunter2")]
		[InlineData("W Auth: Authorization: Bearer abc")]
		[InlineData("I x: enter your Steam Guard code")]
		public void Lines_about_credentials_are_dropped(string line) =>
			Assert.Equal(LogScrubber.OmittedLine + "\n", LogScrubber.Scrub(line));

		[Fact]
		public void Account_name_is_replaced_in_any_case_and_other_text_is_kept() =>
			Assert.Equal("I App: signed in as <account> and <account> ok\n", LogScrubber.Scrub("I App: signed in as MadelineFan and madelinefan ok", "MadelineFan"));

		[Fact]
		public void Jwt_like_strings_steam_ids_emails_and_long_opaque_strings_are_replaced()
		{
			string jwt = "eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJyIn0.c2lnbmF0dXJl";
			string opaque = new('A', 48);
			string result = LogScrubber.Scrub($"I x: id 76561198012345678 mail me@example.com jwt {jwt} blob {opaque} end");
			Assert.Equal("I x: id <steamid> mail <email> jwt <token> blob <redacted> end\n", result);
		}

		[Fact]
		public void Ordinary_log_lines_pass_through() =>
			Assert.Equal("11-09 00:24:54.484 17873 17873 I CelesteAndroid: [CelesteAndroid] startup: sdl-main +258ms\n",
				LogScrubber.Scrub("11-09 00:24:54.484 17873 17873 I CelesteAndroid: [CelesteAndroid] startup: sdl-main +258ms"));

		[Fact]
		public void Short_or_empty_secrets_are_ignored() =>
			Assert.Equal("a b c\n", LogScrubber.Scrub("a b c", "", " ", null, "b"[..0]));
	}
}
