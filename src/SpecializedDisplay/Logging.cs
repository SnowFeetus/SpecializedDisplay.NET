namespace SpecializedDisplay;

/// <summary>
/// Severity for the library's optional log sink. The library never writes to the console or to
/// any IPC channel directly — all diagnostics flow through the <c>Action&lt;LogLevel, string&gt;?</c>
/// passed on <see cref="AcquireOptions"/>. The supervisor parses typed events, not these free-form
/// log lines, so the text is for humans only.
/// </summary>
public enum LogLevel { Info, Warn, Error }
