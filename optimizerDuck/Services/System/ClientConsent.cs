namespace optimizerDuck.Services.System;

/// <summary>
///     Written consent for the phase 7 options (plan: "Opcional, con consentimiento escrito").
///     The app replaces <see cref="Ask" /> at startup with a window that only lets the
///     technician continue after ticking "the client agreed in writing". Anywhere else (tests,
///     no UI) the answer is "no", so nothing optional is ever applied without a person.
/// </summary>
public static class ClientConsent
{
    /// <summary>(title, what will change and how it is undone) → whether the client agreed.</summary>
    public static Func<string, string, Task<bool>> Ask { get; set; } =
        (_, _) => Task.FromResult(false);
}
