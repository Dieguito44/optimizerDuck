using System.IO;
using System.Text;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Services.Diagnostics;
using optimizerDuck.Services.Measurements;

namespace optimizerDuck.Services.Ai;

/// <summary>
///     Plan of improvements F1-07 (audit Z-10, Ley 1581 art. 26): the client's authorization to
///     send the anonymized data to Anthropic. The text the client read is in the resources under
///     <see cref="TextKey" />; its version is stored with the time, so session.json shows exactly
///     what was accepted and when.
/// </summary>
public static class AiConsent
{
    /// <summary>Changes whenever the text changes, so an old record never vouches for a new text.</summary>
    public const string TextVersion = "ley1581-v1-2026-11-19";

    public const string TextKey = "Session.Ai.Consent.Text";

    public static AiConsentRecord Accept(DateTimeOffset nowUtc) =>
        new() { TextVersion = TextVersion, AcceptedAtUtc = nowUtc };

    /// <summary>Whether the session holds a consent to the current text.</summary>
    public static bool IsGiven(ServiceSession session) =>
        session.AiConsent is { TextVersion: TextVersion };
}

public sealed record AiConsentRecord
{
    public required string TextVersion { get; init; }
    public DateTimeOffset AcceptedAtUtc { get; init; }
}

/// <summary>What <see cref="PersonalData" /> takes out of the payload: names and serials.</summary>
public sealed record PersonalTerms(IReadOnlyList<string> Names, IReadOnlyList<string> Serials)
{
    public static readonly PersonalTerms None = new([], []);

    /// <summary>This PC's names, the client label and the BIOS serial.</summary>
    public static PersonalTerms For(ServiceSession session)
    {
        var names = PersonalData.LocalNames().ToList();
        if (!string.IsNullOrWhiteSpace(session.ClientLabel))
            names.Add(session.ClientLabel.Trim());
        var serials = new List<string>();
        try
        {
            if (WarrantyLookup.Read().Serial is { } serial)
                serials.Add(serial);
        }
        catch (Exception)
        {
            // WMI down: the payload's whitelist has no serial field anyway.
        }
        return new PersonalTerms(names, serials);
    }
}

// ---------------------------------------------------------------------------------------------
// Plan F1-08 (audit Z-32), option (a): the API key never touches the client's PC. The client's
// PC writes the anonymized payload to a file; the technician's PC (started with
// --equipo-tecnico) writes the report with the key and saves it to another file; back on the
// client's PC, Zentra takes the report only when it belongs to this session and this payload,
// and checks it again with the same rules as if it had called the AI itself.
// ---------------------------------------------------------------------------------------------

/// <summary>Client's PC → technician's PC: what to write the report from.</summary>
public sealed record AiRequestFile
{
    public const string FormatName = "zentra-ia-solicitud";
    public const string SuggestedName = "zentra-ia-solicitud.json";

    public string Format { get; init; } = FormatName;
    public int SchemaVersion { get; init; } = 1;
    public Guid SessionId { get; init; }
    public required string PayloadSha256 { get; init; }
    public required AiConsentRecord Consent { get; init; }
    public required AiPayload Payload { get; init; }
}

/// <summary>Technician's PC → client's PC: the report written for one request.</summary>
public sealed record AiResultFile
{
    public const string FormatName = "zentra-ia-informe";
    public const string SuggestedName = "zentra-ia-informe.json";

    public string Format { get; init; } = FormatName;
    public int SchemaVersion { get; init; } = 1;
    public Guid SessionId { get; init; }
    public required string PayloadSha256 { get; init; }
    public required AiReportRecord Record { get; init; }
}

public enum AiImportError
{
    Unreadable,
    WrongFile,
    OtherSession,
    OtherPayload,
    NoConsent,
    Rejected,
}

public static class AiExchange
{
    /// <summary>Largest file read: a real one is a few KB.</summary>
    public const long MaxBytes = 512 * 1024;

    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    public static AiRequestFile Request(ServiceSession session, AiPayload payload) =>
        new()
        {
            SessionId = session.Id,
            PayloadSha256 = AiPayloadBuilder.Sha256(payload),
            Consent =
                session.AiConsent
                ?? throw new InvalidOperationException("No consent: nothing may be exported"),
            Payload = payload,
        };

    public static string Serialize<T>(T file) => JsonConvert.SerializeObject(file, Settings);

    public static void Write<T>(string path, T file) =>
        File.WriteAllText(path, Serialize(file), new UTF8Encoding(false));

    /// <summary>The request a technician's PC reads, or null when it is not a valid one.</summary>
    public static AiRequestFile? ReadRequest(string path)
    {
        var request = Read<AiRequestFile>(path);
        return
            request is { Format: AiRequestFile.FormatName, SchemaVersion: 1, Payload: not null }
            && request.Consent is { TextVersion: AiConsent.TextVersion }
            && AiPayloadBuilder.Sha256(request.Payload) == request.PayloadSha256
            ? request
            : null;
    }

    public static AiResultFile Result(AiRequestFile request, AiReportRecord record) =>
        new()
        {
            SessionId = request.SessionId,
            PayloadSha256 = request.PayloadSha256,
            Record = record,
        };

    /// <summary>
    ///     The report for this session, or why not. <paramref name="payload" /> is the one this PC
    ///     builds now: a report written from any other data (an older measurement, another
    ///     client, an edited file) is refused, and an AI text is checked again here.
    /// </summary>
    public static (AiReportRecord? Record, AiImportError? Error) Import(
        string json,
        ServiceSession session,
        AiPayload payload
    )
    {
        AiResultFile? file;
        try
        {
            file = JsonConvert.DeserializeObject<AiResultFile>(json, Settings);
        }
        catch (JsonException)
        {
            return (null, AiImportError.Unreadable);
        }
        if (file is not { Format: AiResultFile.FormatName, SchemaVersion: 1, Record: not null })
            return (null, AiImportError.WrongFile);
        if (file.SessionId != session.Id)
            return (null, AiImportError.OtherSession);
        if (!AiConsent.IsGiven(session))
            return (null, AiImportError.NoConsent);

        var expected = AiPayloadBuilder.Sha256(payload);
        if (
            file.PayloadSha256 != expected
            || file.Record.Payload is null
            || AiPayloadBuilder.Sha256(file.Record.Payload) != expected
        )
            return (null, AiImportError.OtherPayload);

        if (
            file.Record.Report is null
            || (
                file.Record.Source == ReportSource.Ai
                && AiReportValidator.Validate(file.Record.Report, payload).Count > 0
            )
        )
            return (null, AiImportError.Rejected);

        // This PC's own payload goes in the record, not the file's copy (they are equal anyway).
        return (file.Record with { Payload = payload }, null);
    }

    public static (AiReportRecord? Record, AiImportError? Error) ImportFile(
        string path,
        ServiceSession session,
        AiPayload payload
    )
    {
        var text = ReadText(path);
        return text is null ? (null, AiImportError.Unreadable) : Import(text, session, payload);
    }

    private static T? Read<T>(string path)
        where T : class
    {
        var text = ReadText(path);
        if (text is null)
            return null;
        try
        {
            return JsonConvert.DeserializeObject<T>(text, Settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
