using System.Text.Json.Serialization;

namespace DotnetClaw.Telegram;

// ============================================================================
//  Telegram Bot API — Minimal Domain Models
//  Covers only the fields needed for send/receive.
//  All properties use snake_case JSON names to match the Bot API.
// ============================================================================

/// <summary>Wrapper around every Bot API response envelope.</summary>
public sealed record TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; init; }
}

/// <summary>
/// An incoming update from Telegram's getUpdates endpoint.
/// DotnetClaw only handles <c>message</c> and <c>edited_message</c> updates.
/// </summary>
public sealed record TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }

    [JsonPropertyName("edited_message")]
    public TelegramMessage? EditedMessage { get; init; }

    /// <summary>The effective message — prefers message, falls back to edited_message.</summary>
    [JsonIgnore]
    public TelegramMessage? EffectiveMessage => Message ?? EditedMessage;
}

/// <summary>A Telegram message with the fields DotnetClaw cares about.</summary>
public sealed record TelegramMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; init; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }

    [JsonPropertyName("chat")]
    public TelegramChat Chat { get; init; } = null!;

    [JsonPropertyName("date")]
    public long Date { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>UTC timestamp of the message.</summary>
    [JsonIgnore]
    public DateTimeOffset SentAt => DateTimeOffset.FromUnixTimeSeconds(Date);
}

/// <summary>Telegram user (bot or human).</summary>
public sealed record TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; init; }

    [JsonPropertyName("first_name")]
    public string FirstName { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonIgnore]
    public string DisplayName => Username is not null ? $"@{Username}" : FirstName;
}

/// <summary>Telegram chat (private, group, supergroup, or channel).</summary>
public sealed record TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;  // "private" | "group" | "supergroup" | "channel"

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonIgnore]
    public string DisplayName => Title ?? Username ?? Id.ToString();
}

/// <summary>Bot identity returned by getMe.</summary>
public sealed record TelegramBotInfo
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string FirstName { get; init; } = string.Empty;

    [JsonPropertyName("can_read_all_group_messages")]
    public bool CanReadAllGroupMessages { get; init; }
}

// ── Outbound request payloads ─────────────────────────────────────────────────

/// <summary>Payload for sendMessage.</summary>
internal sealed record SendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public required long ChatId { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("parse_mode")]
    public string? ParseMode { get; init; }

    [JsonPropertyName("reply_to_message_id")]
    public long? ReplyToMessageId { get; init; }

    [JsonPropertyName("disable_web_page_preview")]
    public bool DisableWebPagePreview { get; init; } = true;
}

/// <summary>Payload for sendChatAction (typing indicator).</summary>
internal sealed record SendChatActionRequest
{
    [JsonPropertyName("chat_id")]
    public required long ChatId { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = "typing";
}
