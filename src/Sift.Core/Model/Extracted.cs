namespace Sift.Core.Model;

public enum ExtractionMethod { MerchantRule, GenericHeuristic, LocalModel, Derived, UserEdited }

/// <summary>
/// A value found in (or derived from) an email, together with where it came from.
/// A field that was not found is a null <see cref="Extracted{T}"/>, never a placeholder value.
/// </summary>
/// <param name="Confidence">0..1</param>
/// <param name="SourceSnippet">Short excerpt shown in the review UI. Never logged.</param>
public sealed record Extracted<T>(
    T Value,
    ExtractionMethod Method,
    double Confidence,
    string? SourceSnippet = null);
