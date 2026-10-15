using Sift.Core.Extraction;

namespace Sift.Tests;

public sealed class MoneyParserTests
{
    [Theory]
    [InlineData("$1,234.56", "USD", "1234.56")]
    [InlineData("1.234,56 €", "EUR", "1234.56")]
    [InlineData("12,50 €", "EUR", "12.50")]
    [InlineData("¥12,800", "JPY", "12800")]
    [InlineData("$1,250", "USD", "1250")]          // USD has 2 decimals, so ",250" must be grouping
    [InlineData("KWD 12.500", "KWD", "12.500")]    // KWD has 3 decimals
    [InlineData("-$6.35", "USD", "-6.35")]
    [InlineData("$0", "USD", "0")]
    public void Parses_amounts_independently_of_machine_culture(string text, string currency, string expected)
    {
        var tokens = MoneyParser.FindTokens(text);
        var format = MoneyParser.InferFormat(tokens, Currencies.MinorUnits(currency));

        Assert.True(MoneyParser.TryParse(Assert.Single(tokens, t => t.IsMoney), format, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void Three_digits_after_separator_with_unknown_currency_is_ambiguous()
    {
        var tokens = MoneyParser.FindTokens("Total $1,250");
        var format = MoneyParser.InferFormat(tokens, minorUnits: null);

        Assert.True(format.Ambiguous);
        Assert.False(MoneyParser.TryParse(tokens.Single(t => t.IsMoney), format, out _));
    }

    [Fact]
    public void Other_amounts_on_the_receipt_resolve_the_separator()
    {
        var tokens = MoneyParser.FindTokens("Laptop $1,250\tCable $4.50");
        var format = MoneyParser.InferFormat(tokens, minorUnits: null);

        Assert.Equal('.', format.DecimalSeparator);
        Assert.True(MoneyParser.TryParse(tokens.First(t => t.IsMoney), format, out var value));
        Assert.Equal(1250m, value);
    }

    [Theory]
    [InlineData("Latte x2")]
    [InlineData("Order #NW-48213")]
    [InlineData("Visa ending in 4242")]
    [InlineData("Order date: 2026-10-02")]
    [InlineData("VAT (7%)")]
    public void Quantities_ids_years_and_percentages_are_not_money(string text)
    {
        Assert.DoesNotContain(MoneyParser.FindTokens(text), t => t.IsMoney);
    }
}
