using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cassis.Utilities
{
    /// <summary>
    /// Input validation utilities
    /// </summary>
    public static class InputValidation
    {
        public static (bool isValid, int normalizedPort, string? error) ValidatePort(int port)
        {
            if (port < 1024)
                return (false, port, "Port must be 1024 or higher (reserved ports)");

            if (port > 65535)
                return (false, port, "Port must be 65535 or lower");

            return (true, port, null);
        }

        public static (bool isValid, int normalizedRangeEnd, string? error) ValidatePortRange(int startPort, int rangeEnd)
        {
            var (startValid, normalizedStart, startError) = ValidatePort(startPort);
            if (!startValid)
            {
                return (false, normalizedStart, startError);
            }

            var (endValid, normalizedEnd, endError) = ValidatePort(rangeEnd);
            if (!endValid)
            {
                return (false, normalizedEnd, endError);
            }

            if (normalizedEnd < normalizedStart)
            {
                return (false, normalizedEnd, "Port range end must be greater than or equal to the starting port");
            }

            if (normalizedEnd - normalizedStart > 50)
            {
                return (false, normalizedEnd, "Port range is too large (limit 50 ports)");
            }

            return (true, normalizedEnd, null);
        }

        public static (bool isValid, string normalizedPath, string? error) ValidateBasePath(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return (false, "", "Base path cannot be empty");

            var normalized = basePath.Trim();

            if (!normalized.StartsWith("/"))
                normalized = "/" + normalized;

            if (normalized.Length > 1 && normalized.EndsWith("/"))
                normalized = normalized.TrimEnd('/');

            if (normalized.Contains(" "))
                return (false, normalized, "Base path cannot contain spaces");

            if (normalized.Contains(".."))
                return (false, normalized, "Base path cannot contain '..' segments");

            return (true, normalized, null);
        }

        public static bool RequiresRestart(int currentPort, int newPort, string currentBasePath, string newBasePath)
        {
            return currentPort != newPort || currentBasePath != newBasePath;
        }

        /// <summary>
        /// Extracts parameter, handling JTokens (case-insensitive)
        /// </summary>
        public static T GetParameter<T>(Dictionary<string, object> parameters, string paramName, T defaultValue = default(T))
        {
            if (parameters == null) return defaultValue;

            var actualKey = parameters.Keys.FirstOrDefault(k =>
                string.Equals(k, paramName, StringComparison.OrdinalIgnoreCase));

            if (actualKey == null || !parameters.TryGetValue(actualKey, out var value))
                return defaultValue;

            return ConvertParameterValue<T>(value, defaultValue);
        }

        public static T GetOptionalParameter<T>(Dictionary<string, object> parameters, string paramName, T defaultValue = default(T))
        {
            return GetParameter(parameters, paramName, defaultValue);
        }

        /// <summary>
        /// Throws if parameter missing
        /// </summary>
        public static T GetRequiredParameter<T>(Dictionary<string, object> parameters, string paramName)
        {
            if (parameters == null)
                throw new ArgumentException($"Required parameter '{paramName}' is missing (parameters is null).");

            var actualKey = parameters.Keys.FirstOrDefault(k =>
                string.Equals(k, paramName, StringComparison.OrdinalIgnoreCase));

            if (actualKey == null || !parameters.TryGetValue(actualKey, out var value))
                throw new ArgumentException($"Required parameter '{paramName}' is missing.");

            try
            {
                return ConvertParameterValue<T>(value, default(T));
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Parameter '{paramName}' has invalid type. Expected {typeof(T).Name}. Value: {value?.GetType().Name ?? "null"}", ex);
            }
        }

        private static T ConvertParameterValue<T>(object value, T defaultValue)
        {
            if (value == null) return defaultValue;

            try
            {
                if (value is JToken jToken)
                {
                    if (jToken.Type == JTokenType.Null)
                        return defaultValue;

                    if (typeof(T) == typeof(string))
                        return (T)(object)jToken.ToString();

                    if (typeof(T) == typeof(int) && jToken.Type == JTokenType.Integer)
                        return (T)(object)jToken.Value<int>();

                    if (typeof(T) == typeof(double) && (jToken.Type == JTokenType.Float || jToken.Type == JTokenType.Integer))
                        return (T)(object)jToken.Value<double>();

                    if (typeof(T) == typeof(bool) && jToken.Type == JTokenType.Boolean)
                        return (T)(object)jToken.Value<bool>();

                    return (T)Convert.ChangeType(jToken.ToString(), typeof(T));
                }

                if (value is T directValue)
                    return directValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        public static (bool valid, string? error) ValidateComponentId(string? componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return (false, "Component ID cannot be empty");
            if (!Guid.TryParse(componentId, out _))
                return (false, $"Invalid component ID format: '{componentId}' is not a valid GUID");
            return (true, null);
        }

        public static (bool valid, string? error) ValidateScriptCode(string? code, int maxLength = 1_000_000)
        {
            if (code == null)
                return (false, "Script code cannot be null");
            if (code.Length > maxLength)
                return (false, $"Script code exceeds maximum length of {maxLength:N0} characters");
            if (code.Contains("\0"))
                return (false, "Script code contains invalid null characters");
            return (true, null);
        }

        public static (bool valid, string? error) ValidateCoordinate(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return (false, $"{name} must be a finite number");
            const double MaxCoord = 1e12;
            if (Math.Abs(value) > MaxCoord)
                return (false, $"{name} exceeds maximum coordinate value of {MaxCoord:E0}");
            return (true, null);
        }

        public static (bool valid, string? error) ValidateNickname(string? nickname)
        {
            if (string.IsNullOrWhiteSpace(nickname))
                return (false, "Nickname cannot be empty");
            if (nickname.Length > 256)
                return (false, "Nickname exceeds maximum length of 256 characters");
            return (true, null);
        }
    }
}
