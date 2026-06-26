using System;

namespace chat;

public sealed record ChatWireMessage(
    string Kind,
    string? Text,
    string? FileName,
    string? ContentType,
    long Size,
    string? DataBase64,
    DateTimeOffset SentAt)
{
    public const string TextKind = "text";
    public const string FileKind = "file";
}
