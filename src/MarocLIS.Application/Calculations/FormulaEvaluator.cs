using System.Globalization;
using MarocLIS.Domain.Common;

namespace MarocLIS.Application.Calculations;

/// <summary>
/// Evaluates the calculation formulas attached to derived parameters.
/// <para>
/// A laboratory needs to define LDL by Friedewald, an albumin/globulin ratio, an eGFR or a
/// corrected calcium without a developer, so formulas are data. That makes the expression language
/// a security boundary: this is a purpose-built recursive-descent parser over a closed grammar, not
/// a general scripting engine. It cannot reach the file system, the network or the database, and an
/// unknown identifier is an error rather than something to resolve at runtime.
/// </para>
/// <para>
/// Grammar: parameter references in square brackets (<c>[CHOL]</c>), decimal literals, the
/// operators <c>+ - * / ^</c> with conventional precedence, parentheses, and the functions
/// <c>min max abs round sqrt ln log10 pow if</c>. <c>if(cond; a; b)</c> together with the
/// comparisons <c>&lt; &lt;= &gt; &gt;= = &lt;&gt;</c> covers sex- or threshold-dependent formulas.
/// </para>
/// <para>
/// Function arguments are separated by a <b>semicolon</b>, not a comma, and both <c>.</c> and
/// <c>,</c> are accepted as the decimal separator. That is not a stylistic choice: a Moroccan
/// biologist writes <c>1,5</c>, and with a comma as the argument separator <c>round(1,5)</c> would
/// be genuinely ambiguous between one argument and two. Semicolons remove the ambiguity — the same
/// resolution a French-locale spreadsheet uses, so the convention is already familiar.
/// </para>
/// </summary>
public static class FormulaEvaluator
{
    /// <summary>
    /// Identifiers that resolve from the patient and the order rather than from a measured
    /// parameter, so a formula can depend on age or sex. Supplied by the calculation service
    /// alongside the measured values; excluded from the "unknown parameter" check because they
    /// are not catalogue entries.
    /// </summary>
    public static class ContextVariables
    {
        /// <summary>Age in whole years at the date of sampling.</summary>
        public const string AgeYears = "AGE";

        /// <summary>Age in days — needed by neonatal formulas.</summary>
        public const string AgeDays = "AGE_JOURS";

        /// <summary>1 when the patient is female, 0 otherwise.</summary>
        public const string IsFemale = "SEXE_F";

        /// <summary>1 when the patient is male, 0 otherwise.</summary>
        public const string IsMale = "SEXE_M";

        /// <summary>1 when a pregnancy is recorded, 0 otherwise.</summary>
        public const string IsPregnant = "GROSSESSE";

        /// <summary>Weight in kilograms; 0 when not recorded.</summary>
        public const string WeightKg = "POIDS";

        /// <summary>Height in centimetres; 0 when not recorded.</summary>
        public const string HeightCm = "TAILLE";

        /// <summary>Body surface area in m² (Mosteller); 0 when weight or height is missing.</summary>
        public const string BodySurfaceArea = "SURFACE_CORP";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            AgeYears, AgeDays, IsFemale, IsMale, IsPregnant, WeightKg, HeightCm, BodySurfaceArea
        };

        public static bool Is(string identifier) => All.Contains(identifier);
    }

    /// <summary>
    /// Evaluates <paramref name="formula"/> against the supplied parameter values.
    /// Returns a failure rather than throwing: a bad formula is a configuration error the
    /// biologist must see, not an exception the result-entry screen should die on.
    /// </summary>
    public static Result<decimal> Evaluate(string formula, IReadOnlyDictionary<string, decimal> values)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return Result.Failure<decimal>(Error.Validation("Formula_Empty"));

        try
        {
            var parser = new Parser(formula, values);
            var result = parser.ParseExpression();
            parser.ExpectEnd();

            // No finiteness check: unlike double, decimal has no infinity and no NaN. The two ways a
            // formula can leave the representable range — a division by zero and an overflow — are
            // raised as exceptions by the runtime and handled below, so there is no non-finite value
            // left here to reject.
            return Result.Success(result);
        }
        catch (FormulaException ex)
        {
            return Result.Failure<decimal>(Error.Validation(ex.ResourceKey, ex.Detail ?? string.Empty));
        }
        catch (DivideByZeroException)
        {
            return Result.Failure<decimal>(Error.Validation("Formula_DivisionByZero"));
        }
        catch (OverflowException)
        {
            return Result.Failure<decimal>(Error.Validation("Formula_Overflow"));
        }
    }

    /// <summary>
    /// Parameter codes a formula depends on. Used to order the evaluation of derived parameters and
    /// to detect a circular definition before it is saved.
    /// </summary>
    public static IReadOnlyList<string> ReferencedParameters(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula)) return [];

        var found = new List<string>();
        var index = 0;

        while (index < formula.Length)
        {
            var open = formula.IndexOf('[', index);
            if (open < 0) break;

            var close = formula.IndexOf(']', open + 1);
            if (close < 0) break;

            var code = formula[(open + 1)..close].Trim().ToUpperInvariant();
            if (code.Length > 0 && !found.Contains(code, StringComparer.Ordinal)) found.Add(code);

            index = close + 1;
        }

        return found;
    }

    /// <summary>
    /// Checks a formula without evaluating it: syntax plus the existence of every referenced
    /// parameter. Called when the catalogue entry is saved so a broken formula never reaches the
    /// bench.
    /// </summary>
    public static Result Validate(string formula, IReadOnlySet<string> knownParameterCodes)
    {
        var referenced = ReferencedParameters(formula);

        var unknown = referenced
            .Where(c => !knownParameterCodes.Contains(c) && !ContextVariables.Is(c))
            .ToList();

        if (unknown.Count > 0)
            return Result.Failure(Error.Validation("Formula_UnknownParameter", string.Join(", ", unknown)));

        // Evaluate with placeholder values purely to exercise the parser. Ones rather than zeros so
        // a legitimate division by a parameter does not report a false division-by-zero.
        var probe = referenced.ToDictionary(c => c, _ => 1m, StringComparer.Ordinal);
        var evaluation = Evaluate(formula, probe);

        return evaluation.IsSuccess ||
               evaluation.FirstError.ResourceKey is "Formula_DivisionByZero" or "Formula_NotFinite"
            ? Result.Success()
            : Result.Failure(evaluation.FirstError);
    }

    /// <summary>
    /// Orders derived parameters so each is computed after the ones it reads. Returns a failure when
    /// the definitions are circular, which is the failure mode worth catching at configuration time.
    /// </summary>
    public static Result<IReadOnlyList<string>> ResolveEvaluationOrder(
        IReadOnlyDictionary<string, string> formulasByParameterCode)
    {
        var ordered = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 in progress, 2 done

        Result Visit(string code)
        {
            if (state.TryGetValue(code, out var mark))
            {
                if (mark == 1) return Result.Failure(Error.Validation("Formula_CircularReference", code));
                return Result.Success();
            }

            if (ContextVariables.Is(code)) return Result.Success();

            if (!formulasByParameterCode.TryGetValue(code, out var formula))
                return Result.Success(); // A measured parameter: nothing to order.

            state[code] = 1;

            foreach (var dependency in ReferencedParameters(formula))
            {
                var visited = Visit(dependency);
                if (visited.IsFailure) return visited;
            }

            state[code] = 2;
            ordered.Add(code);
            return Result.Success();
        }

        foreach (var code in formulasByParameterCode.Keys)
        {
            var visited = Visit(code);
            if (visited.IsFailure) return Result.Failure<IReadOnlyList<string>>(visited.FirstError);
        }

        return Result.Success<IReadOnlyList<string>>(ordered);
    }

    private sealed class FormulaException(string resourceKey, string? detail = null) : Exception(resourceKey)
    {
        public string ResourceKey { get; } = resourceKey;
        public string? Detail { get; } = detail;
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, decimal> values)
    {
        private int _position;

        public decimal ParseExpression() => ParseComparison();

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_position < text.Length)
                throw new FormulaException("Formula_UnexpectedToken", text[_position..]);
        }

        // comparison := additive (( "<" | "<=" | ">" | ">=" | "=" | "<>" ) additive)?
        // A comparison yields 1 or 0 so it can feed if().
        private decimal ParseComparison()
        {
            var left = ParseAdditive();
            SkipWhitespace();

            var op = PeekComparisonOperator();
            if (op is null) return left;

            _position += op.Length;
            var right = ParseAdditive();

            var truth = op switch
            {
                "<" => left < right,
                "<=" => left <= right,
                ">" => left > right,
                ">=" => left >= right,
                "=" => left == right,
                "<>" => left != right,
                _ => throw new FormulaException("Formula_UnexpectedToken", op)
            };

            return truth ? 1m : 0m;
        }

        private string? PeekComparisonOperator()
        {
            if (_position >= text.Length) return null;

            if (_position + 1 < text.Length)
            {
                var two = text.Substring(_position, 2);
                if (two is "<=" or ">=" or "<>") return two;
            }

            var one = text[_position];
            return one switch
            {
                '<' or '>' or '=' => one.ToString(),
                _ => null
            };
        }

        // additive := multiplicative (("+" | "-") multiplicative)*
        private decimal ParseAdditive()
        {
            var value = ParseMultiplicative();

            while (true)
            {
                SkipWhitespace();
                if (_position >= text.Length) return value;

                var op = text[_position];
                if (op is not ('+' or '-')) return value;

                _position++;
                var right = ParseMultiplicative();
                value = op == '+' ? value + right : value - right;
            }
        }

        // multiplicative := power (("*" | "/") power)*
        private decimal ParseMultiplicative()
        {
            var value = ParsePower();

            while (true)
            {
                SkipWhitespace();
                if (_position >= text.Length) return value;

                var op = text[_position];
                if (op is not ('*' or '/')) return value;

                _position++;
                var right = ParsePower();

                if (op == '*')
                {
                    value *= right;
                }
                else
                {
                    if (right == 0m) throw new DivideByZeroException();
                    value /= right;
                }
            }
        }

        // power := unary ("^" power)?   — right-associative
        private decimal ParsePower()
        {
            var value = ParseUnary();
            SkipWhitespace();

            if (_position < text.Length && text[_position] == '^')
            {
                _position++;
                var exponent = ParsePower();
                return Pow(value, exponent);
            }

            return value;
        }

        private decimal ParseUnary()
        {
            SkipWhitespace();
            if (_position >= text.Length) throw new FormulaException("Formula_UnexpectedEnd");

            switch (text[_position])
            {
                case '-':
                    _position++;
                    return -ParseUnary();
                case '+':
                    _position++;
                    return ParseUnary();
                default:
                    return ParsePrimary();
            }
        }

        private decimal ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= text.Length) throw new FormulaException("Formula_UnexpectedEnd");

            var current = text[_position];

            if (current == '(')
            {
                _position++;
                var value = ParseExpression();
                Expect(')');
                return value;
            }

            if (current == '[') return ParseParameterReference();

            if (char.IsDigit(current) || current is '.' or ',') return ParseNumber();

            if (char.IsLetter(current) || current == '_') return ParseFunction();

            throw new FormulaException("Formula_UnexpectedToken", current.ToString());
        }

        private decimal ParseParameterReference()
        {
            _position++; // consume '['
            var close = text.IndexOf(']', _position);
            if (close < 0) throw new FormulaException("Formula_UnclosedParameterReference");

            var code = text[_position..close].Trim().ToUpperInvariant();
            _position = close + 1;

            if (code.Length == 0) throw new FormulaException("Formula_EmptyParameterReference");

            if (!values.TryGetValue(code, out var value))
                throw new FormulaException("Formula_MissingValue", code);

            return value;
        }

        private decimal ParseNumber()
        {
            var start = _position;

            while (_position < text.Length &&
                   (char.IsDigit(text[_position]) || text[_position] is '.' or ','))
                _position++;

            // Accept both decimal separators: a biologist typing a formula will use whichever the
            // keyboard gives them, and the meaning is unambiguous here.
            var literal = text[start.._position].Replace(',', '.');

            if (!decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormulaException("Formula_InvalidNumber", literal);

            return value;
        }

        private decimal ParseFunction()
        {
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
                _position++;

            var name = text[start.._position].ToLowerInvariant();
            Expect('(');

            var args = new List<decimal>();
            SkipWhitespace();

            if (_position < text.Length && text[_position] == ')')
            {
                _position++;
            }
            else
            {
                while (true)
                {
                    args.Add(ParseExpression());
                    SkipWhitespace();

                    if (_position >= text.Length) throw new FormulaException("Formula_UnexpectedEnd");

                    // Semicolon, not comma: a comma is a decimal separator here.
                    if (text[_position] == ';')
                    {
                        _position++;
                        continue;
                    }

                    Expect(')');
                    break;
                }
            }

            return Apply(name, args);
        }

        private static decimal Apply(string name, List<decimal> args)
        {
            void Arity(int expected)
            {
                if (args.Count != expected)
                    throw new FormulaException("Formula_WrongArgumentCount", $"{name}/{expected}");
            }

            switch (name)
            {
                case "min":
                    if (args.Count < 2) throw new FormulaException("Formula_WrongArgumentCount", "min/2+");
                    return args.Min();

                case "max":
                    if (args.Count < 2) throw new FormulaException("Formula_WrongArgumentCount", "max/2+");
                    return args.Max();

                case "abs":
                    Arity(1);
                    return Math.Abs(args[0]);

                case "round":
                    if (args.Count is not (1 or 2))
                        throw new FormulaException("Formula_WrongArgumentCount", "round/1..2");
                    var digits = args.Count == 2 ? (int)args[1] : 0;
                    if (digits is < 0 or > 15)
                        throw new FormulaException("Formula_InvalidArgument", "round");
                    return Math.Round(args[0], digits, MidpointRounding.AwayFromZero);

                case "sqrt":
                    Arity(1);
                    if (args[0] < 0m) throw new FormulaException("Formula_InvalidArgument", "sqrt");
                    return (decimal)Math.Sqrt((double)args[0]);

                case "ln":
                    Arity(1);
                    if (args[0] <= 0m) throw new FormulaException("Formula_InvalidArgument", "ln");
                    return (decimal)Math.Log((double)args[0]);

                case "log10":
                    Arity(1);
                    if (args[0] <= 0m) throw new FormulaException("Formula_InvalidArgument", "log10");
                    return (decimal)Math.Log10((double)args[0]);

                case "pow":
                    Arity(2);
                    return Pow(args[0], args[1]);

                case "if":
                    Arity(3);
                    return args[0] != 0m ? args[1] : args[2];

                default:
                    throw new FormulaException("Formula_UnknownFunction", name);
            }
        }

        private static decimal Pow(decimal value, decimal exponent)
        {
            // Integer exponents stay in decimal so money-like and small-integer powers are exact;
            // fractional exponents (an eGFR formula uses −1.154, −0.203) go through double.
            if (decimal.Truncate(exponent) == exponent && Math.Abs(exponent) <= 28m)
            {
                var power = (int)exponent;
                if (power == 0) return 1m;

                var result = 1m;
                for (var i = 0; i < Math.Abs(power); i++) result *= value;
                return power > 0 ? result : 1m / result;
            }

            var computed = Math.Pow((double)value, (double)exponent);
            if (double.IsNaN(computed) || double.IsInfinity(computed))
                throw new FormulaException("Formula_NotFinite");

            return (decimal)computed;
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (_position >= text.Length || text[_position] != expected)
                throw new FormulaException("Formula_Expected", expected.ToString());
            _position++;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }
    }
}
