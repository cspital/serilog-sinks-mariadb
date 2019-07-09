﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.MariaDB.Sinks
{
    internal sealed class MariaDBSinkCore
    {
        private readonly string _tableName;
        private readonly IFormatProvider _formatProvider;
        private readonly MariaDBSinkOptions _options;

        public MariaDBSinkCore(
            string connectionString,
            IFormatProvider formatProvider,
            MariaDBSinkOptions options,
            string tableName,
            bool autoCreateTable
        )
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            _tableName = tableName;
            _formatProvider = formatProvider;
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (autoCreateTable)
            {
                try
                {
                    var tableCreator = new SqlTableCreator(connectionString, _tableName, _options.PropertiesToColumnsMapping.Values);
                    tableCreator.CreateTable();
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine($"Exception creating table {_tableName}:\n{ex}");
                }
            }
        }

        public IEnumerable<KeyValuePair<string, object>> GetColumnsAndValues(LogEvent logEvent)
        {
            foreach (var map in _options.PropertiesToColumnsMapping)
            {
                if (map.Key.Equals("Message", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new KeyValuePair<string, object>(map.Value, logEvent.RenderMessage(_formatProvider));
                    continue;
                }

                if (map.Key.Equals("MessageTemplate", StringComparison.OrdinalIgnoreCase))
                {
                    if (_options.HashMessageTemplate)
                    {
                        using (var hasher = SHA256.Create())
                        {
                            var buffer = logEvent.MessageTemplate.Text.Select(i => (byte) i).ToArray();
                            var hash = hasher.ComputeHash(buffer);
                            var result = string.Join(string.Empty, hash.Select(i => i.ToString("x2")));

                            yield return new KeyValuePair<string, object>(map.Value, result);
                            continue;
                        }
                    }

                    yield return new KeyValuePair<string, object>(map.Value, logEvent.MessageTemplate.Text);
                    continue;
                }

                if (map.Key.Equals("Level", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new KeyValuePair<string, object>(map.Value, _options.EnumsAsInts ? (object)logEvent.Level : logEvent.Level.ToString());
                    continue;
                }

                if (map.Key.Equals("Timestamp", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new KeyValuePair<string, object>(map.Value, _options.TimestampInUtc ? logEvent.Timestamp.ToUniversalTime().DateTime : logEvent.Timestamp.DateTime);
                    continue;
                }

                if (map.Key.Equals("Exception", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new KeyValuePair<string, object>(map.Value, logEvent.Exception?.ToString());
                    continue;
                }

                if (map.Key.Equals("Properties", StringComparison.OrdinalIgnoreCase))
                {
                    var properties = logEvent.Properties.AsEnumerable();

                    if (_options.ExcludePropertiesWithDedicatedColumn)
                    {
                        properties = properties
                            .Where(i => !_options.PropertiesToColumnsMapping.ContainsKey(i.Key));
                    }

                    yield return new KeyValuePair<string, object>(map.Value, _options.PropertiesFormatter(
                        new ReadOnlyDictionary<string, LogEventPropertyValue>(
                            properties.ToDictionary(k => k.Key, v => v.Value)
                        )
                    ));

                    continue;
                }

                if (!logEvent.Properties.TryGetValue(map.Key, out var property))
                {
                    yield return new KeyValuePair<string, object>(map.Value, null);
                    continue;
                }

                if (!(property is ScalarValue scalarValue))
                {
                    var sb = new StringBuilder();
                    using (var writer = new StringWriter(sb))
                    {
                        property.Render(writer, formatProvider: _formatProvider);
                    }

                    yield return new KeyValuePair<string, object>(map.Value, sb.ToString());
                    continue;
                }

                if (scalarValue.Value == null)
                {
                    yield return new KeyValuePair<string, object>(map.Value, DBNull.Value);
                    continue;
                }

                var isEnum = scalarValue.Value is Enum;

                if (isEnum && !_options.EnumsAsInts)
                {
                    yield return new KeyValuePair<string, object>(map.Value, scalarValue.Value.ToString());
                    continue;
                }

                yield return new KeyValuePair<string, object>(map.Value, scalarValue.Value);
            }
        }

        public string GetBulkInsertStatement(IList<IEnumerable<KeyValuePair<string, object>>> columnValues)
        {
            var commandText = new StringBuilder();
            AppendInsertStatement(commandText);

            for (var i = 0; i < columnValues.Count; i++)
            {
                if (i != 0)
                {
                    commandText.AppendLine(",");
                }

                AppendValuesPart(commandText, columnValues[i], i);
            }

            return commandText.ToString();
        }

        public string GetInsertStatement(IEnumerable<KeyValuePair<string, object>> columnValues)
        {
            var commandText = new StringBuilder();

            AppendInsertStatement(commandText);
            AppendValuesPart(commandText, columnValues);

            return commandText.ToString();
        }

        public void AppendInsertStatement(StringBuilder output)
        {
            var columnNames = _options.PropertiesToColumnsMapping
                .Select(i => i.Value)
                .ToList();

            output.AppendLine($"INSERT INTO {_tableName} ({string.Join(", ", columnNames)})");
            output.AppendLine("VALUES");
        }

        public void AppendValuesPart(StringBuilder output, IEnumerable<KeyValuePair<string, object>> columnValues, int? identifier = null)
        {
            var parameters = columnValues
                .Select(i => i.Value == null ? "DEFAULT" : $"@{i.Key}{(identifier.HasValue ? identifier.ToString() : "")}")
                .ToList();

            output.Append("(");
            output.Append(string.Join(", ", parameters));
            output.Append(")");
        }
    }
}
