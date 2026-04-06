using Autodesk.Revit.DB;
using SmartCon.Core.General;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace PipeConnect.Core.General
{
    public class ExpressionEvaluator
    {
        public enum TokenType
        {
            Number,
            Operator,
            Function,
            LeftParenthesis,
            RightParenthesis,
            Parameter
        }

        public class Token
        {
            public TokenType Type { get; set; }
            public string Value { get; set; }
        }

        private FamilyInstance _familyInstance;
        private Dictionary<string, double> _variables;

        public ExpressionEvaluator(FamilyInstance familyInstance)
        {
            _familyInstance = familyInstance;
            _variables = new Dictionary<string, double>();
        }

        public ExpressionEvaluator()
        {
            _variables = new Dictionary<string, double>();
        }

        public void SetVariable(string name, double value)
        {
            _variables[name] = value;
        }

        public double Evaluate(string expression)
        {
            expression = StringPlus.RemoveUnits(expression);
            var tokens = Tokenize(expression);
            var rpn = ConvertToRPN(tokens);
            return EvaluateRPN(rpn);
        }

        private List<Token> Tokenize(string expression)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < expression.Length)
            {
                char c = expression[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || c == '.')
                {
                    StringBuilder sb = new StringBuilder();
                    bool hasDecimalPoint = false;

                    while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                    {
                        if (expression[i] == '.')
                        {
                            if (hasDecimalPoint)
                                throw new Exception("Неверный формат числа: более одной десятичной точки.");
                            hasDecimalPoint = true;
                        }
                        sb.Append(expression[i]);
                        i++;
                    }
                    tokens.Add(new Token { Type = TokenType.Number, Value = sb.ToString() });
                    continue;
                }

                if (c == '+' || c == '-' || c == '*' || c == '/' || c == '^')
                {
                    tokens.Add(new Token { Type = TokenType.Operator, Value = c.ToString() });
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    tokens.Add(new Token { Type = TokenType.LeftParenthesis, Value = c.ToString() });
                    i++;
                    continue;
                }

                if (c == ')')
                {
                    tokens.Add(new Token { Type = TokenType.RightParenthesis, Value = c.ToString() });
                    i++;
                    continue;
                }

                if (char.IsLetter(c) || c == '[')
                {
                    StringBuilder sb = new StringBuilder();
                    if (c == '[')
                    {
                        while (i < expression.Length && expression[i] != ']')
                        {
                            sb.Append(expression[i]);
                            i++;
                        }
                        if (i < expression.Length)
                        {
                            sb.Append(expression[i]); // добавляем закрывающую скобку
                            i++;
                        }
                        tokens.Add(new Token { Type = TokenType.Parameter, Value = sb.ToString() });
                    }
                    else
                    {
                        while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_' || expression[i] == ' '))
                        {
                            sb.Append(expression[i]);
                            i++;
                        }

                        string identifier = sb.ToString().Trim();

                        if (i < expression.Length && expression[i] == '(')
                        {
                            tokens.Add(new Token { Type = TokenType.Function, Value = identifier });
                        }
                        else
                        {
                            tokens.Add(new Token { Type = TokenType.Parameter, Value = identifier });
                        }
                    }
                    continue;
                }

                throw new Exception($"Неизвестный символ в выражении: {c}");
            }

            return tokens;
        }

        private bool IsOperatorOrSeparator(char c)
        {
            return c == '+' || c == '-' || c == '*' || c == '/' || c == '^' || c == '(' || c == ')' || c == ',';
        }

        private List<Token> ConvertToRPN(List<Token> tokens)
        {
            var output = new List<Token>();
            var stack = new Stack<Token>();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Number:
                    case TokenType.Parameter:
                        output.Add(token);
                        break;

                    case TokenType.Function:
                        stack.Push(token);
                        break;

                    case TokenType.Operator:
                        while (stack.Count > 0 && stack.Peek().Type == TokenType.Operator &&
                               ((GetOperatorPrecedence(token.Value) <= GetOperatorPrecedence(stack.Peek().Value)) ||
                                (GetOperatorPrecedence(token.Value) < GetOperatorPrecedence(stack.Peek().Value))))
                        {
                            output.Add(stack.Pop());
                        }
                        stack.Push(token);
                        break;

                    case TokenType.LeftParenthesis:
                        stack.Push(token);
                        break;

                    case TokenType.RightParenthesis:
                        while (stack.Count > 0 && stack.Peek().Type != TokenType.LeftParenthesis)
                        {
                            output.Add(stack.Pop());
                        }

                        if (stack.Count > 0 && stack.Peek().Type == TokenType.LeftParenthesis)
                        {
                            stack.Pop();
                        }
                        else
                        {
                            throw new Exception("Несбалансированные скобки в выражении.");
                        }

                        if (stack.Count > 0 && stack.Peek().Type == TokenType.Function)
                        {
                            output.Add(stack.Pop());
                        }
                        break;
                }
            }

            while (stack.Count > 0)
            {
                var token = stack.Pop();
                if (token.Type == TokenType.LeftParenthesis || token.Type == TokenType.RightParenthesis)
                {
                    throw new Exception("Несбалансированные скобки в выражении.");
                }
                output.Add(token);
            }

            return output;
        }

        private double EvaluateRPN(List<Token> tokens)
        {
            var stack = new Stack<double>();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Number:
                        stack.Push(double.Parse(token.Value, CultureInfo.InvariantCulture));
                        break;

                    case TokenType.Parameter:
                        if (token.Value.StartsWith("[") && token.Value.EndsWith("]"))
                        {
                            string paramName = token.Value.Substring(1, token.Value.Length - 2);
                            double paramValue = GetParameterValue(paramName);
                            stack.Push(paramValue);
                        }
                        else if (_variables.ContainsKey(token.Value))
                        {
                            stack.Push(_variables[token.Value]);
                        }
                        else
                        {
                            throw new Exception($"Неизвестный параметр: {token.Value}");
                        }
                        break;

                    case TokenType.Operator:
                        if (stack.Count < 2)
                            throw new Exception("Недостаточно операндов для оператора: " + token.Value);

                        double b = stack.Pop();
                        double a = stack.Pop();

                        switch (token.Value)
                        {
                            case "+":
                                stack.Push(a + b);
                                break;
                            case "-":
                                stack.Push(a - b);
                                break;
                            case "*":
                                stack.Push(a * b);
                                break;
                            case "/":
                                if (b == 0)
                                    throw new Exception("Деление на ноль.");
                                stack.Push(a / b);
                                break;
                            case "^":
                                stack.Push(Math.Pow(a, b));
                                break;
                            default:
                                throw new Exception("Неизвестный оператор: " + token.Value);
                        }
                        break;

                    case TokenType.Function:
                        switch (token.Value.ToLower())
                        {
                            case "sin":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: sin");
                                stack.Push(Math.Sin(stack.Pop()));
                                break;
                            case "cos":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: cos");
                                stack.Push(Math.Cos(stack.Pop()));
                                break;
                            case "tan":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: tan");
                                stack.Push(Math.Tan(stack.Pop()));
                                break;
                            case "sqrt":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: sqrt");
                                double val = stack.Pop();
                                if (val < 0)
                                    throw new Exception("Квадратный корень из отрицательного числа.");
                                stack.Push(Math.Sqrt(val));
                                break;
                            case "abs":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: abs");
                                stack.Push(Math.Abs(stack.Pop()));
                                break;
                            case "round":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: round");
                                stack.Push(Math.Round(stack.Pop()));
                                break;
                            case "floor":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: floor");
                                stack.Push(Math.Floor(stack.Pop()));
                                break;
                            case "ceiling":
                            case "ceil":
                                if (stack.Count < 1)
                                    throw new Exception("Недостаточно операндов для функции: ceiling");
                                stack.Push(Math.Ceiling(stack.Pop()));
                                break;
                            case "max":
                                if (stack.Count < 2)
                                    throw new Exception("Недостаточно операндов для функции: max");
                                double maxB = stack.Pop();
                                double maxA = stack.Pop();
                                stack.Push(Math.Max(maxA, maxB));
                                break;
                            case "min":
                                if (stack.Count < 2)
                                    throw new Exception("Недостаточно операндов для функции: min");
                                double minB = stack.Pop();
                                double minA = stack.Pop();
                                stack.Push(Math.Min(minA, minB));
                                break;
                            case "pi":
                                stack.Push(Math.PI);
                                break;
                            case "e":
                                stack.Push(Math.E);
                                break;
                            default:
                                throw new Exception("Неизвестная функция: " + token.Value);
                        }
                        break;
                }
            }

            if (stack.Count != 1)
                throw new Exception("Ошибка в выражении: несбалансированное количество операндов и операторов.");

            return stack.Pop();
        }

        private int GetOperatorPrecedence(string op)
        {
            switch (op)
            {
                case "+":
                case "-":
                    return 1;
                case "*":
                case "/":
                    return 2;
                case "^":
                    return 3;
                default:
                    return 0;
            }
        }

        private double GetParameterValue(string paramName)
        {
            if (_familyInstance == null)
                throw new Exception("FamilyInstance не инициализирован.");

            var param = _familyInstance.LookupParameter(paramName);
            if (param == null)
                throw new Exception($"Параметр не найден: {paramName}");

            return param.AsDouble();
        }
    }
} 