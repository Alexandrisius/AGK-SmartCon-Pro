using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PipeConnect.Core
{
    public class StringParsers
    {
        /// <summary>
        /// Удаляет массив подстрок из строки
        /// </summary>
        /// <param name="input"></param>
        /// <param name="substringsToRemove"></param>
        /// <returns></returns>
        public static string RemoveSubstrings(string input, string[] substringsToRemove)
        {
            foreach (var substring in substringsToRemove)
            {
                input = input.Replace(substring, string.Empty);
            }
            return input;
        }

        /// <summary>
        /// Метод возвращает значение переменной параметра семейства которое приведёт к необходимому результату "result".
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="elem"></param>
        /// <param name="param"></param>
        /// <param name="result"></param>
        /// <param name="inExpParam"></param>
        /// <returns></returns>
        public static double LightRevitFormulaSolver(Document doc, Element elem, Parameter param, double result, out Parameter inExpParam)
        {
            inExpParam = null;

            if (elem is FamilyInstance familyInstance)
            {
                FamilyManager familyManager = doc.EditFamily(familyInstance.Symbol.Family).FamilyManager;

                FamilyParameter famParam = familyManager.get_Parameter(param.Definition.Name);

                string tempStrForTrimStart = null;
                string tempStrForTrimEnd = null;
                double tempDouble = 0.0;

                if (famParam.Formula != null)
                {
                    string formulaParam = RemoveSubstrings(famParam.Formula, [ " мм", " см", " м", " дм", " mm", " cm", " m",
                         " дюйм", " in", " 'фут", " ft", " '"]);// возможно написать проверку будет ли парситься имя параметра после удаления единицы измерения.
                                                                // Чтобы не получилось такого что кто-то неграмотно назвал параметр с пробелом и одним из вариантов ед. из.



                    if (formulaParam.Contains('['))
                    // если у нас плохо названа переменная и ревит подставил квадратные скобки чтобы ограничить влияние знаков
                    {
                        string paramName = formulaParam.Substring(formulaParam.IndexOf('['), formulaParam.IndexOf(']') + 1 - formulaParam.IndexOf('['));

                        formulaParam = formulaParam.Replace(paramName, "x");

                        formulaParam = RemoveSubstrings(famParam.Formula, [" / 1)", " * 1)", " / 1 ", " * 1 ", "(", ")"]);
                        // удалить операции над единицами измерения Revit и вызванные ими скобки


                        famParam = familyManager.get_Parameter(paramName.Substring(1, paramName.Length - 2));

                        inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                        if (formulaParam.Contains('/') && formulaParam.Count(c => c == '/') == 1)
                        {
                            string[] parameters = formulaParam.Split('/');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);

                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }

                            }
                            return result * tempDouble;

                        }
                        if (formulaParam.Contains('*') && formulaParam.Count(c => c == '*') == 1)
                        {
                            string[] parameters = formulaParam.Split('*');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);

                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }

                            }
                            return result / tempDouble;

                        }
                        if (formulaParam.Contains('+') && formulaParam.Count(c => c == '+') == 1)
                        {
                            string[] parameters = formulaParam.Split('+');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);

                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }

                            }
                            return result - tempDouble;

                        }
                        if (formulaParam.Contains('-') && formulaParam.Count(c => c == '-') == 1)
                        {
                            string[] parameters = formulaParam.Split('-');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);

                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }

                            }
                            return result + tempDouble;

                        }
                        if (formulaParam.Contains('^') && formulaParam.Count(c => c == '^') == 1)
                        {
                            string[] parameters = formulaParam.Split('-');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);

                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }

                            }
                            return Math.Pow(result, 1 / tempDouble);

                        }
                        if (!formulaParam.Contains('/') && !formulaParam.Contains('*') && !formulaParam.Contains('+') &&
                            !formulaParam.Contains('-') && !formulaParam.Contains('^'))
                        {
                            return result;
                        }



                    }

                    else if (!formulaParam.Contains('['))
                    {
                        formulaParam = RemoveSubstrings(famParam.Formula, [" / 1)", " * 1)", " / 1 ", " * 1 ", "(", ")"]);
                        // удалить операции над единицами измерения Revit и вызванные ими скобки


                        if (formulaParam.Contains('/') && formulaParam.Count(c => c == '/') == 1)
                        //если у нас лёгкая формула с одним операторов деления
                        {

                            string[] parameters = formulaParam.Split('/');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);


                                if (!double.TryParse(tempStrForTrimEnd, out _))
                                {
                                    famParam = familyManager.get_Parameter(tempStrForTrimEnd);

                                    inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                                }
                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }


                            }
                            return result * tempDouble;

                        }
                        if (formulaParam.Contains('*') && formulaParam.Count(c => c == '*') == 1)
                        //если у нас лёгкая формула с одним операторов умножения
                        {
                            string[] parameters = formulaParam.Split('*');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);


                                if (!double.TryParse(tempStrForTrimEnd, out _))
                                {
                                    famParam = familyManager.get_Parameter(tempStrForTrimEnd);

                                    //inExpParam = familyInstance.get_Parameter(famParam.Definition);
                                    inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                                }
                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }


                            }
                            return result / tempDouble;

                        }
                        if (formulaParam.Contains('+') && formulaParam.Count(c => c == '+') == 1)
                        //если у нас лёгкая формула с одним операторов сложения
                        {
                            string[] parameters = formulaParam.Split('+');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);


                                if (!double.TryParse(tempStrForTrimEnd, out _))
                                {
                                    famParam = familyManager.get_Parameter(tempStrForTrimEnd);

                                    //inExpParam = familyInstance.get_Parameter(famParam.Definition);
                                    inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                                }
                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }


                            }
                            return result - tempDouble;

                        }
                        if (formulaParam.Contains('-') && formulaParam.Count(c => c == '-') == 1)
                        //если у нас лёгкая формула с одним операторов вычитания
                        {
                            string[] parameters = formulaParam.Split('-');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);


                                if (!double.TryParse(tempStrForTrimEnd, out _))
                                {
                                    famParam = familyManager.get_Parameter(tempStrForTrimEnd);

                                    //inExpParam = familyInstance.get_Parameter(famParam.Definition);
                                    inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                                }
                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }


                            }
                            return result + tempDouble;

                        }
                        if (formulaParam.Contains('^') && formulaParam.Count(c => c == '^') == 1)
                        //если у нас лёгкая формула с одним операторов вычитания
                        {
                            string[] parameters = formulaParam.Split('^');
                            foreach (var splitParam in parameters)
                            {

                                tempStrForTrimStart = splitParam.TrimStart([' ']); // удаляю все проблемы в начале строки
                                formulaParam = formulaParam.Replace(splitParam, tempStrForTrimStart);

                                tempStrForTrimEnd = tempStrForTrimStart.TrimEnd([' ']);// удаляю все проблемы в конце строки
                                formulaParam = formulaParam.Replace(tempStrForTrimStart, tempStrForTrimEnd);


                                if (!double.TryParse(tempStrForTrimEnd, out _))
                                {
                                    famParam = familyManager.get_Parameter(tempStrForTrimEnd);

                                    //inExpParam = familyInstance.get_Parameter(famParam.Definition);
                                    inExpParam = familyInstance.LookupParameter(famParam.Definition.Name);

                                }
                                if (double.TryParse(tempStrForTrimEnd, out tempDouble)) { }


                            }
                            return Math.Pow(result, 1 / tempDouble);


                        }
                        if (!formulaParam.Contains('/') && !formulaParam.Contains('*') && !formulaParam.Contains('+') &&
                            !formulaParam.Contains('-') && !formulaParam.Contains('^'))
                        {

                            inExpParam = familyInstance.LookupParameter(formulaParam);

                            return result;
                        }

                    }



                    return -1;
                }
                else
                    return -1;
            }
            else
                return -1;
        }

        /// <summary>
        /// Данный метод возвращает массив строк названий параметров в формуле size_lookup и при наличии прямого обращения к таблице возвращает название данной таблицы.
        /// </summary>
        /// <param name="formula"></param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        public static List<string> LightSizeLookupFormulaParser(string formula, out string tableName)
        {
            List<string> result = new List<string>();

            tableName = null;
            if (formula == null || !formula.Contains("size_lookup(")) { return null; }

            string currentParam = "";

            formula = formula.Substring(formula.IndexOf("size_lookup(") + 11);// 11 - это количество символов подстроки до символа скобки.

            int countParenthesis = 0;
            foreach (var item_char in formula)
            {
                if (item_char == ',' && countParenthesis == 1)
                {
                    if (currentParam.Contains('[') && currentParam.Contains(']'))
                    {
                        currentParam = currentParam.Trim([' ', '[', ']']);
                    }
                    result.Add(currentParam.Trim());
                    currentParam = "";
                    continue;
                }
                if (item_char == '(')
                {
                    countParenthesis++;
                    continue;
                }
                else if (item_char == ')')
                {
                    countParenthesis--;
                    if (countParenthesis == 0)
                    {
                        if (currentParam.Contains('[') && currentParam.Contains(']'))
                        {
                            currentParam = currentParam.Trim([' ','[', ']']);
                        }
                        result.Add(currentParam.Trim());
                        break;
                    }
                    continue;
                }

                currentParam += item_char;
            }

            if (result[0].Contains('"'))
            {
                tableName = result[0].Trim([' ', '"']);
            }


            return result;

        }

        /// <summary>
        /// Парсер формул. Возвращает список имён которые могут быть названиями параметров
        /// </summary>
        /// <param name="formula"></param>
        /// <returns></returns>
        public static List<string> LightRevitFormulaParser(string formula)
        {
            List<string> listParamName = new List<string>();

            var listSL = LightSizeLookupFormulaParser(formula, out _);
            if (listSL != null)
            {
                return listSL;
            }

            formula = RemoveSubstrings(formula, [ " мм", " см", " м", " дм", " mm", " cm", " m",
                         " дюйм", " in", " 'фут", " ft", " '"]);

            formula = RemoveSubstrings(formula, [" / 1)", " * 1)", " / 1 ", " * 1 ", "(", ")"]);


            var list = formula.Split([ ',', '+', '-', '*', '/', '^' ]);


            foreach ( var item in list)
            {
                listParamName.Add(item.Trim([' ', '[', ']']));
            }

            return listParamName;

        }
    }
}
