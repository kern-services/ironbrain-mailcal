namespace Ironbrain.Email;

public sealed class EmailSummary
{
    public required string Id { get; set; }
    public required string Subject { get; set; }
    public required string From { get; set; }
    public DateTimeOffset Date { get; set; }
    public bool IsSeen { get; set; }
}

public sealed class EmailContent
{
    public required string Id { get; set; }
    public required string Subject { get; set; }
    public required string From { get; set; }
    public required string BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public DateTimeOffset Date { get; set; }
}

public sealed class EmailSendRequest
{
    public required string To { get; set; }
    public required string Subject { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
}


