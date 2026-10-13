using FluentAssertions;
using MarocLIS.Application.Calculations;
using Xunit;

namespace MarocLIS.Tests.Calculations;

/// <summary>
/// Tests for the calculated-parameter expression engine.
/// <para>
/// Formulas are data a biologist edits, which makes the parser a security boundary as well as a
/// correctness one: an unknown identifier or function must be an error, never something resolved at
/// runtime. The tests cover the real formulas the catalogue seeds — Friedewald LDL and CKD-EPI eGFR
/// — because those are what will actually be running in production.
/// </para>
/// </summary>
public sealed class FormulaEvaluatorTests
{
    private static Dictionary<string, decimal> Values(params (string Code, decimal Value)[] pairs) =>
        pairs.ToDictionary(p => p.Code, p => p.Value, StringComparer.Ordinal);

    /// <summary>
    /// For the formulas that reference no parameter. Named rather than <c>[]</c>: a collection
    /// expression needs a constructible target type, and the parameter is an
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, decimal> NoValues =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    // ---- Arithmetic and precedence -----------------------------------------------------------------

    [Theory]
    [InlineData("2 + 3 * 4", 14)]                 // multiplication binds tighter
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("10 - 4 - 3", 3)]                 // subtraction is left-associative
    [InlineData("100 / 10 / 2", 5)]
    [InlineData("2 ^ 3 ^ 2", 512)]                // exponentiation is right-associative
    [InlineData("-5 + 10", 5)]
    [InlineData("2 * -3", -6)]
    public void Arithmetic_FollowsConventionalPrecedence(string formula, decimal expected) =>
        FormulaEvaluator.Evaluate(formula, NoValues).Value.Should().Be(expected);

    [Theory]
    [InlineData("1.5 + 1.5", 3)]
    [InlineData("1,5 + 1,5", 3)]                  // a comma is a decimal separator, not a separator
    public void BothDecimalSeparators_AreAccepted(string formula, decimal expected) =>
        FormulaEvaluator.Evaluate(formula, NoValues).Value.Should().Be(expected);

    [Fact]
    public void ArgumentSeparator_IsTheSemicolon()
    {
        // With a comma as the argument separator, round(1,5) would be genuinely ambiguous between
        // one argument and two. Semicolons remove the ambiguity — the French-locale convention.
        FormulaEvaluator.Evaluate("round(1,555; 2)", NoValues).Value.Should().Be(1.56m);
        FormulaEvaluator.Evaluate("min(3; 7; 5)", NoValues).Value.Should().Be(3m);
        FormulaEvaluator.Evaluate("max(3; 7; 5)", NoValues).Value.Should().Be(7m);
    }

    // ---- Parameter references ------------------------------------------------------------------------

    [Fact]
    public void ParameterReferences_AreResolvedFromTheSuppliedValues()
    {
        var result = FormulaEvaluator.Evaluate(
            "[CHOL] - [HDL]", Values(("CHOL", 2.10m), ("HDL", 0.50m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1.60m);
    }

    [Fact]
    public void ParameterReferences_AreCaseInsensitiveAndTrimmed()
    {
        var result = FormulaEvaluator.Evaluate("[ chol ]", Values(("CHOL", 2m)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2m);
    }

    [Fact]
    public void AMissingInputValue_IsAFailureAndNotAZero()
    {
        // Substituting zero would silently produce a plausible-looking wrong result, which is far
        // worse than leaving the calculated parameter awaited.
        var result = FormulaEvaluator.Evaluate("[CHOL] - [HDL]", Values(("CHOL", 2m)));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Formula_MissingValue");
    }

    [Fact]
    public void ReferencedParameters_AreExtractedForDependencyOrdering()
    {
        var referenced = FormulaEvaluator.ReferencedParameters(
            "if([TG]<4; round([CHOL]-[HDL]-[TG]/5; 2); 0)");

        referenced.Should().BeEquivalentTo(["TG", "CHOL", "HDL"]);
    }

    // ---- Friedewald LDL, as the catalogue seeds it -----------------------------------------------------

    [Fact]
    public void FriedewaldLdl_MatchesTheHandCalculation()
    {
        const string formula = "if([TG]<4; round([CHOL]-[HDL]-[TG]/5; 2); 0)";

        // 2.10 − 0.50 − 1.20/5 = 2.10 − 0.50 − 0.24 = 1.36
        var result = FormulaEvaluator.Evaluate(
            formula, Values(("CHOL", 2.10m), ("HDL", 0.50m), ("TG", 1.20m)));

        result.Value.Should().Be(1.36m);
    }

    [Fact]
    public void FriedewaldLdl_YieldsZeroAboveTheValidTriglycerideRange()
    {
        // Friedewald is not valid above 4 g/L of triglycerides. The formula enforces its own
        // limit rather than reporting a number the method cannot support.
        const string formula = "if([TG]<4; round([CHOL]-[HDL]-[TG]/5; 2); 0)";

        var result = FormulaEvaluator.Evaluate(
            formula, Values(("CHOL", 3.00m), ("HDL", 0.40m), ("TG", 5.50m)));

        result.Value.Should().Be(0m);
    }

    // ---- CKD-EPI eGFR, as the catalogue seeds it -------------------------------------------------------

    [Fact]
    public void CkdEpiEgfr_UsesTheContextVariablesAndProducesAPlausibleValue()
    {
        // Verbatim from StarterCatalogue. CREAT is reported in mg/L and CKD-EPI wants mg/dL, so
        // the single conversion is /10; this read /10/88.4 and inflated every eGFR by ~6.3×.
        const string formula =
            "round(141 * pow(min([CREAT]/10/if([SEXE_F]=1;0.7;0.9);1);if([SEXE_F]=1;-0.329;-0.411))" +
            " * pow(max([CREAT]/10/if([SEXE_F]=1;0.7;0.9);1);-1.209)" +
            " * pow(0.993;[AGE]) * if([SEXE_F]=1;1.018;1);0)";

        // A 50-year-old man with a creatinine of 9 mg/L (≈ 80 µmol/L) sits in the normal range.
        var male = FormulaEvaluator.Evaluate(formula, Values(
            ("CREAT", 9m),
            (FormulaEvaluator.ContextVariables.IsFemale, 0m),
            (FormulaEvaluator.ContextVariables.AgeYears, 50m)));

        male.IsSuccess.Should().BeTrue();
        male.Value.Should().BeInRange(80m, 110m);

        // The same creatinine in a woman gives a lower eGFR: the equation's sex coefficients.
        var female = FormulaEvaluator.Evaluate(formula, Values(
            ("CREAT", 9m),
            (FormulaEvaluator.ContextVariables.IsFemale, 1m),
            (FormulaEvaluator.ContextVariables.AgeYears, 50m)));

        female.IsSuccess.Should().BeTrue();
        female.Value.Should().BeLessThan(male.Value);
    }

    [Fact]
    public void FractionalExponents_AreSupported()
    {
        // The eGFR equation needs −1.209 and −0.329, so a decimal-only power would not do.
        var result = FormulaEvaluator.Evaluate("pow(4; 0.5)", NoValues);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeApproximately(2m, 0.0001m);
    }

    // ---- Comparisons and conditionals -------------------------------------------------------------------

    [Theory]
    [InlineData("if(1 < 2; 10; 20)", 10)]
    [InlineData("if(2 < 1; 10; 20)", 20)]
    [InlineData("if(2 <= 2; 10; 20)", 10)]
    [InlineData("if(3 > 2; 10; 20)", 10)]
    [InlineData("if(2 >= 3; 10; 20)", 20)]
    [InlineData("if(2 = 2; 10; 20)", 10)]
    [InlineData("if(2 <> 2; 10; 20)", 20)]
    public void Conditionals_EvaluateEachComparisonOperator(string formula, decimal expected) =>
        FormulaEvaluator.Evaluate(formula, NoValues).Value.Should().Be(expected);

    // ---- Functions -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abs(-7)", 7)]
    [InlineData("sqrt(9)", 3)]
    [InlineData("round(2.4567; 2)", 2.46)]
    [InlineData("round(2.5)", 3)]
    [InlineData("min(1; 2)", 1)]
    [InlineData("max(1; 2)", 2)]
    public void Functions_ProduceTheExpectedValues(string formula, decimal expected) =>
        FormulaEvaluator.Evaluate(formula, NoValues).Value.Should().Be(expected);

    [Theory]
    [InlineData("sqrt(-1)", "Formula_InvalidArgument")]
    [InlineData("ln(0)", "Formula_InvalidArgument")]
    [InlineData("log10(-5)", "Formula_InvalidArgument")]
    [InlineData("abs(1; 2)", "Formula_WrongArgumentCount")]
    [InlineData("min(1)", "Formula_WrongArgumentCount")]
    public void Functions_RejectInvalidUse(string formula, string expectedKey) =>
        FormulaEvaluator.Evaluate(formula, NoValues).FirstError.ResourceKey.Should().Be(expectedKey);

    // ---- Failure modes are returned, never thrown --------------------------------------------------------

    [Theory]
    [InlineData("", "Formula_Empty")]
    [InlineData("1 / 0", "Formula_DivisionByZero")]
    [InlineData("2 +", "Formula_UnexpectedEnd")]
    [InlineData("[UNCLOSED", "Formula_UnclosedParameterReference")]
    [InlineData("[]", "Formula_EmptyParameterReference")]
    [InlineData("frobnicate(2)", "Formula_UnknownFunction")]
    [InlineData("2 @ 3", "Formula_UnexpectedToken")]
    [InlineData("(2 + 3", "Formula_Expected")]
    public void BadFormulas_ReturnAFailureRatherThanThrowing(string formula, string expectedKey)
    {
        var result = FormulaEvaluator.Evaluate(formula, NoValues);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be(expectedKey);
    }

    [Fact]
    public void DivisionByAParameterThatIsZero_IsAFailure()
    {
        var result = FormulaEvaluator.Evaluate("[A] / [B]", Values(("A", 5m), ("B", 0m)));

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Formula_DivisionByZero");
    }

    // ---- Validation at configuration time -----------------------------------------------------------------

    [Fact]
    public void Validate_AcceptsAFormulaWhoseParametersAllExist()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "CHOL", "HDL", "TG" };

        FormulaEvaluator.Validate("[CHOL]-[HDL]-[TG]/5", known)
                        .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsAnUnknownParameter()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "CHOL" };

        var result = FormulaEvaluator.Validate("[CHOL]-[NOPE]", known);

        result.IsFailure.Should().BeTrue();
        result.FirstError.ResourceKey.Should().Be("Formula_UnknownParameter");
    }

    [Fact]
    public void Validate_AcceptsTheContextVariables()
    {
        // AGE and SEXE_F are supplied from the patient, not from the catalogue, so they must not
        // be reported as unknown parameters.
        var known = new HashSet<string>(StringComparer.Ordinal) { "CREAT" };

        FormulaEvaluator.Validate("[CREAT] * [AGE] * [SEXE_F]", known)
                        .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_ToleratesADivisionByAParameterThatProbesAsZero()
    {
        // Validation probes with placeholder values; a legitimate division must not be reported as
        // a configuration error just because the probe happened to divide by zero.
        var known = new HashSet<string>(StringComparer.Ordinal) { "A", "B" };

        FormulaEvaluator.Validate("[A]/[B]", known).IsSuccess.Should().BeTrue();
    }

    // ---- Dependency ordering ----------------------------------------------------------------------------------

    [Fact]
    public void EvaluationOrder_PlacesDependenciesFirst()
    {
        // RATIO depends on LDL, which depends on CHOL, HDL and TG. Evaluating in catalogue order
        // would compute RATIO from a stale LDL.
        var formulas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RATIO"] = "[LDL] / [HDL]",
            ["LDL"] = "[CHOL] - [HDL] - [TG]/5"
        };

        var order = FormulaEvaluator.ResolveEvaluationOrder(formulas);

        order.IsSuccess.Should().BeTrue();
        order.Value.Should().ContainInOrder("LDL", "RATIO");
    }

    [Fact]
    public void EvaluationOrder_DetectsACircularDefinition()
    {
        var formulas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "[B] + 1",
            ["B"] = "[A] + 1"
        };

        var order = FormulaEvaluator.ResolveEvaluationOrder(formulas);

        order.IsFailure.Should().BeTrue();
        order.FirstError.ResourceKey.Should().Be("Formula_CircularReference");
    }

    [Fact]
    public void EvaluationOrder_DetectsASelfReference()
    {
        var formulas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "[A] * 2"
        };

        FormulaEvaluator.ResolveEvaluationOrder(formulas).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void EvaluationOrder_IgnoresMeasuredParametersAndContextVariables()
    {
        var formulas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DFG"] = "[CREAT] * [AGE]"
        };

        var order = FormulaEvaluator.ResolveEvaluationOrder(formulas);

        order.IsSuccess.Should().BeTrue();
        order.Value.Should().Equal("DFG");
    }
}
