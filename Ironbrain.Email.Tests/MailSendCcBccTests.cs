using Ironbrain.Email;
using MimeKit;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailSendCcBccTests
{
	[Fact]
	public void BuildSendMime_PassesCcAndBccToMimeMessage()
	{
		var request = new EmailSendRequest
		{
			To = "alice@example.com",
			Cc = "bob@example.com, carol@example.com",
			Bcc = "secret@example.com",
			Subject = "Hello",
			TextBody = "body"
		};

		var mime = MailKitEmailService.BuildSendMime(request, "me@example.com", "Me");

		Assert.Equal("alice@example.com", mime.To.Mailboxes.Single().Address);
		Assert.Equal(
			["bob@example.com", "carol@example.com"],
			mime.Cc.Mailboxes.Select(m => m.Address).ToArray());
		Assert.Equal("secret@example.com", mime.Bcc.Mailboxes.Single().Address);
		Assert.Equal("Hello", mime.Subject);
	}

	[Fact]
	public void BuildSendMime_OmitsEmptyCcBcc()
	{
		var request = new EmailSendRequest
		{
			To = "alice@example.com",
			Subject = "Hi"
		};

		var mime = MailKitEmailService.BuildSendMime(request, "me@example.com");

		Assert.Empty(mime.Cc);
		Assert.Empty(mime.Bcc);
	}

	[Fact]
	public void AddAddresses_RejectsEmptyAngleAddress()
	{
		var list = new InternetAddressList();
		Assert.Throws<ArgumentException>(() => MailKitEmailService.AddAddresses(list, "<>"));
	}
}
