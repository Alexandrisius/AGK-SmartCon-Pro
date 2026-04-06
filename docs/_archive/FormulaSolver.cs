using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PipeConnect.Core.General;

namespace SmartCon.Core.General
{
    public class FormulaSolver
    {
        private readonly FamilyInstance _familyInstance;

        public FormulaSolver(FamilyInstance familyInstance)
        {
            _familyInstance = familyInstance;
        }

        public string SolveFormula(string formula, string unknownVariable = null, bool calculate = true)
        {
            try
            {
                if (formula.Contains("if(") || formula.Contains("If("))
                {
                    formula = ConditionSimplifier(_familyInstance, formula);
                }

                var paramMatches = Regex.Matches(formula, @"\[([^\]]+)\]");
                string processedFormula = formula;

                foreach (Match match in paramMatches)
                {
                    string paramName = match.Groups[1].Value;
                    if (paramName != unknownVariable)
                    {
                        double? paramValue = GetParameterValue(_familyInstance, paramName);
                        if (paramValue.HasValue)
                        {
                            processedFormula = processedFormula.Replace(match.Value, 
                                paramValue.Value.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }

                if (calculate && !IsEquation(processedFormula, unknownVariable))
                {
                    var evaluator = new ExpressionEvaluator();
                    double result = evaluator.Evaluate(processedFormula);
                    return result.ToString(CultureInfo.InvariantCulture);
                }

                return processedFormula;
            }
            catch (Exception ex)
            {
                return formula;
            }
        }

        private static string ConditionSimplifier(FamilyInstance familyInstance, string formula)
        {
            try
            {
                var ifRegex = new Regex(@"if\s*\((.*?),(.*?),(.*?)\)", RegexOptions.IgnoreCase);
                while (ifRegex.IsMatch(formula))
                {
                    formula = ifRegex.Replace(formula, match =>
                    {
                        var condition = match.Groups[1].Value.Trim();
                        var trueValue = match.Groups[2].Value.Trim();
                        var falseValue = match.Groups[3].Value.Trim();

                        var conditionResult = EvaluateCondition(familyInstance, condition);
                        return conditionResult ? trueValue : falseValue;
                    });
                }
                return formula;
            }
            catch (Exception)
            {
                return formula;
            }
        }

        private static bool EvaluateCondition(FamilyInstance familyInstance, string condition)
        {
            try
            {
                var comparisonRegex = new Regex(@"(.*?)(==|!=|<=|>=|<|>)(.*)");
                var match = comparisonRegex.Match(condition);

                if (!match.Success)
                    return false;

                var leftStr = match.Groups[1].Value.Trim();
                var op = match.Groups[2].Value;
                var rightStr = match.Groups[3].Value.Trim();

                double? left = EvaluateValue(familyInstance, leftStr);
                double? right = EvaluateValue(familyInstance, rightStr);

                if (!left.HasValue || !right.HasValue)
                    return false;

                switch (op)
                {
                    case "==": return Math.Abs(left.Value - right.Value) < 1e-10;
                    case "!=": return Math.Abs(left.Value - right.Value) >= 1e-10;
                    case "<=": return left.Value <= right.Value;
                    case ">=": return left.Value >= right.Value;
                    case "<": return left.Value < right.Value;
                    case ">": return left.Value > right.Value;
                    default: return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static double? EvaluateValue(FamilyInstance familyInstance, string expression)
        {
            try
            {
                var paramMatches = Regex.Matches(expression, @"\[([^\]]+)\]");
                string processedFormula = expression;

                foreach (Match match in paramMatches)
                {
                    string paramName = match.Groups[1].Value;
                    double? paramValue = GetParameterValue(familyInstance, paramName);
                    if (paramValue.HasValue)
                    {
                        processedFormula = processedFormula.Replace(match.Value, 
                            paramValue.Value.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        return null;
                    }
                }

                if (double.TryParse(processedFormula, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                {
                    return result;
                }

                var evaluator = new ExpressionEvaluator();
                return evaluator.Evaluate(processedFormula);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? GetParameterValue(FamilyInstance familyInstance, string paramName)
        {
            try
            {
                var param = familyInstance.LookupParameter(paramName);
                if (param != null)
                {
                    return param.AsDouble();
                }
            }
            catch { }
            return null;
        }

        private bool IsEquation(string formula, string unknownVariable)
        {
            if (string.IsNullOrEmpty(unknownVariable))
                return false;

            return formula.Contains("=") && formula.Contains(unknownVariable);
        }
    }
} 