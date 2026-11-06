using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Romd.Persistence.ReferenceData;

/// <summary>NFKD, Unicode letter/number retention and lowercase search, translated entirely into PostgreSQL.</summary>
internal static class ReferenceSearch
{
    public static string Normalize(string value) => throw new InvalidOperationException("Evaluate this function in PostgreSQL.");
    public static void Configure(ModelBuilder builder, IRelationalTypeMappingSource typeMappings)
    {
        var text = typeMappings.FindMapping(typeof(string)) ?? throw new InvalidOperationException("The provider has no string mapping.");
        var pattern = new SqlConstantExpression(ExcludedCharacters(), text);
        builder.HasDbFunction(typeof(ReferenceSearch).GetMethod(nameof(Normalize))!).HasTranslation(args =>
        {
            var normalized = new SqlFunctionExpression("normalize", [args[0], new SqlFragmentExpression("NFKD")], true,
                [true, false], typeof(string), text);
            var lower = new SqlFunctionExpression("lower", [new CollateExpression(normalized, "pg_c_utf8")], true, [true], typeof(string), text);
            return new SqlFunctionExpression("regexp_replace", [new CollateExpression(lower, "C"), pattern,
                new SqlConstantExpression("", text),
                new SqlConstantExpression("g", text)], true,
                [true, false, false, false], typeof(string), text);
        });
    }
    // Explicit Unicode ranges avoid a database locale's different POSIX alnum classification.
    private static string ExcludedCharacters()
    {
        var result = new StringBuilder("[^");
        int? start = null;
        for (var point = 0; point <= 0x110000; point++)
        {
            var included = Rune.IsValid(point) && Rune.GetUnicodeCategory(new Rune(point)) is
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            if (included)
                start ??= point;
            else if (start is { } first)
            {
                result.Append(char.ConvertFromUtf32(first));
                if (point - first > 1)
                    result.Append('-').Append(char.ConvertFromUtf32(point - 1));
                start = null;
            }
        }
        return result.Append(']').ToString();
    }
}
