﻿namespace Maxstupo.YdlUi.Core.Arguments {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text;

    public class ArgumentSerializer : IArgumentSerializer, ICommandLineSerializer {

        private static readonly NLog.ILogger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// A lookup dictionary to translate argument object values to strings. If the argument data type isn't defined in the dictionary, <see cref="Object.ToString()"/> will be used instead.
        /// </summary>
        protected Dictionary<Type, Func<Argument, Type, object, string>> ValueTranslators { get; } = new Dictionary<Type, Func<Argument, Type, object, string>>();

        /// <summary>
        /// A defined function called for all arguments to check if they should be serialized.
        /// </summary>
        protected Func<Argument, Type, object, bool> CommonChecker { get; set; }

        /// <summary>
        /// A lookup dictionary to check if an argument of a specific data type should be serialized.
        /// </summary>
        protected Dictionary<Type, Func<Argument, Type, object, bool>> Checkers { get; } = new Dictionary<Type, Func<Argument, Type, object, bool>>();

        /// <summary>
        /// A dictionary of builders used to create argument strings for specific argument data types.
        /// </summary>
        protected Dictionary<Type, Func<Argument, Type, object, string>> Builders { get; } = new Dictionary<Type, Func<Argument, Type, object, string>>();


        /// <summary>The separator between each argument.</summary>
        protected string ArgumentSeparator { get; set; } = " ";

        /// <summary>The separator between each flag and value.</summary>
        protected string Separator { get; set; } = " ";

        public ArgumentSerializer() {
            Init();
        }

        protected virtual void Init() {
            ValueTranslators.Clear();
            Builders.Clear();
            Checkers.Clear();

            ArgumentSeparator = " ";
            Separator = " ";

            // Only serialize flag if value isn't null. This is called before any other registered serialization checkers.
            CommonChecker = (argument, type, value) => value != null;

            // Only serialize boolean flags if the value is true.
            Checkers.Add(typeof(bool), (argument, type, value) => (bool) value); // Serialize boolean flags if value isn't false.

            // Only serialize dictionaries if they contain items.
            Checkers.Add(typeof(Dictionary<string, string>), (argument, type, value) => ((Dictionary<string, string>) value).Count > 0);

            // Translates string properties to string... duh!
            ValueTranslators.Add(typeof(string), (argument, type, value) => value.ToString());

            // Translates enum properties to string by using the name representing the enum value.
            ValueTranslators.Add(typeof(Enum), (argument, type, value) => {
                string name = Enum.GetName(type, value);

                switch (argument.EnumCase) {
                    case EnumCasePolicy.Lowercase:
                        return name.ToLower();
                    case EnumCasePolicy.Uppercase:
                        return name.ToUpper();
                    case EnumCasePolicy.Default:
                    default:
                        return name;
                }
            });
        }

        public string Serialize(string filename, object argObject) {
            return $"{filename} {Serialize(argObject)}";
        }

        public string Serialize(object argObject) {
            List<BuiltFlag> flags = SerializeObject(argObject).OrderBy(x => x.Argument.Order).ToList();

            StringBuilder sb = new StringBuilder();
            flags.ForEach(flag => sb.Append(flag.Value).Append(ArgumentSeparator));

            if (flags.Count > 0)
                sb.Remove(sb.Length - ArgumentSeparator.Length, ArgumentSeparator.Length);

            return sb.ToString();
        }

        private IEnumerable<BuiltFlag> SerializeObject(object argObject) {
            if (argObject == null)
                yield break;

            Type containerType = argObject.GetType();

            // Get all properties from the arguments object.
            PropertyInfo[] propertyInfos = containerType.GetProperties(BindingFlags.Instance | BindingFlags.Public);

            foreach (PropertyInfo propertyInfo in propertyInfos) {
                Type propertyType = Nullable.GetUnderlyingType(propertyInfo.PropertyType) ?? propertyInfo.PropertyType; // The property type.

                object value = propertyInfo.GetValue(argObject, null); // The value of the property.


                // Only process properties that have an Argument attribute defined.
                Argument argument = propertyInfo.GetCustomAttribute<Argument>();
                if (argument == null) {
                    // Inspect objects that are defined without an argument attribute, and have an ArgumentContainer
                    if (propertyType.IsClass && propertyType.GetCustomAttribute<ArgumentContainer>() != null) {
                        foreach (BuiltFlag flag in SerializeObject(value))
                            yield return flag;
                    }
                    continue;
                }

                if (!ShouldAppendFlag(argument, propertyType, value))
                    continue;

                Type typeKey = propertyType.IsEnum ? typeof(Enum) : propertyType;

                // Build the flag using a custom builder if registered for the property type.
                string builtFlag;
                if (Builders.TryGetValue(typeKey, out Func<Argument, Type, object, string> builder)) {
                    builtFlag = builder(argument, propertyType, value);

                } else { // else build the flag with the registered value translators.
                    string translatedValue = TranslateFlagValue(argument, propertyType, value);
                    builtFlag = BuildFlag(argument, translatedValue);
                }

                if (!string.IsNullOrWhiteSpace(builtFlag))
                    yield return new BuiltFlag(argument, builtFlag);

            }

        }


        protected virtual string TranslateFlagValue(Argument argument, Type type, object value) {
            if (value == null)
                return string.Empty;

            Type typeKey = type.IsEnum ? typeof(Enum) : type;

            if (ValueTranslators.TryGetValue(typeKey, out Func<Argument, Type, object, string> translator))
                return translator(argument, type, value);

            return value.ToString();
        }

        protected virtual bool ShouldAppendFlag(Argument argument, Type type, object value) {

            if (CommonChecker != null && !CommonChecker(argument, type, value))
                return false;

            if (Checkers.TryGetValue(type, out Func<Argument, Type, object, bool> append))
                return append(argument, type, value);

            return true;
        }

        /// <summary>
        /// Returns the combined flag and value, based on the argument template.
        /// </summary>
        /// <param name="argument">The argument that contains the template and flag.</param>
        /// <param name="value">The value of the argument.</param>
        /// <returns>A combined flag and value.</returns>
        protected virtual string BuildFlag(Argument argument, string value) {
            string quotedValue = QuoteValue(argument.QuotePolicy, value);
            string builtFlag = argument.Template;

            return builtFlag
                .Replace("{separator}", Separator.ToString())
                .Replace("{flag}", argument.Flag)
                .Replace("{value}", quotedValue);
        }

        /// <summary>
        /// Quotes and returns the given <paramref name="value"/> if the <paramref name="policy"/> matches the content of the given value.
        /// </summary>
        /// <param name="policy">The <see cref="QuotePolicy"/> to check for.</param>
        /// <param name="value">The string value to apply the <see cref="QuotePolicy"/>.</param>
        /// <returns>A quoted or unquoted variant of <paramref name="value"/>.</returns>
        protected string QuoteValue(QuotePolicy policy, string value) {
            bool containsWhitespace = value.Contains(' ');
            bool notAlphaNumeric = !value.All(char.IsLetterOrDigit);

            if (policy == QuotePolicy.Always || (policy == QuotePolicy.WhenNecessary && (containsWhitespace || notAlphaNumeric)))
                return $"\"{value}\"";

            return value;
        }


        /// <summary>
        /// A container struct that holds an Argument attribute and the parsed value of its property.
        /// </summary>
        protected struct BuiltFlag {
            public Argument Argument { get; }
            public string Value { get; }

            public BuiltFlag(Argument argument, string value) {
                this.Argument = argument;
                this.Value = value;
            }
        }

    }

}