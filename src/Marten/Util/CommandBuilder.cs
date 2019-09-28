using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Baseline;
using Marten.Linq.QueryHandlers;
using Marten.Schema.Arguments;
using Marten.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Marten.Util
{
    public class CommandBuilder: IDisposable
    {
        public static readonly string TenantIdArg = ":" + TenantIdArgument.ArgName;

        public static string BuildJsonStringLocator(string column, MemberInfo[] members, Casing casing = Casing.Default)
        {
            var locator = new StringBuilder(column);
            var depth = 1;
            foreach (var memberInfo in members)
            {
                locator.Append(depth == members.Length ? " ->> " : " -> ");
                locator.Append($"'{memberInfo.Name.FormatCase(casing)}'");
                depth++;
            }
            return locator.ToString();
        }

        public static string BuildJsonObjectLocator(string column, MemberInfo[] members, Casing casing = Casing.Default)
        {
            var locator = new StringBuilder(column);
            foreach (var memberInfo in members)
            {
                locator.Append($" -> '{memberInfo.Name.FormatCase(casing)}'");
            }
            return locator.ToString();
        }

        public static NpgsqlCommand BuildCommand(Action<CommandBuilder> configure)
        {
            var cmd = new NpgsqlCommand();
            using (var builder = new CommandBuilder(cmd))
            {
                configure(builder);

                cmd.CommandText = builder.ToString();
            }

            return cmd;
        }

        public static NpgsqlCommand ToCommand(ITenant tenant, IQueryHandler handler)
        {
            var command = new NpgsqlCommand();

            using (var builder = new CommandBuilder(command))
            {
                handler.ConfigureCommand(builder);
                command.CommandText = builder._sql.ToString();

                if (command.CommandText.Contains(TenantIdArg))
                {
                    command.AddNamedParameter(TenantIdArgument.ArgName, tenant.TenantId);
                }

                return command;
            }
        }

        public static NpgsqlCommand ToBatchCommand(ITenant tenant, IEnumerable<IQueryHandler> handlers)
        {
            if (handlers.Count() == 1)
                return ToCommand(tenant, handlers.Single());

            var wholeStatement = new StringBuilder();
            var command = new NpgsqlCommand();

            foreach (var handler in handlers)
            {
                // Maybe have it use a shared pool here.
                using (var builder = new CommandBuilder(command))
                {
                    handler.ConfigureCommand(builder);
                    if (wholeStatement.Length > 0)
                    {
                        wholeStatement.Append(";");
                    }

                    wholeStatement.Append(builder);
                }
            }

            command.CommandText = wholeStatement.ToString();

            command.AddTenancy(tenant);

            return command;
        }

        // TEMP -- will shift this to being pooled later
        private readonly StringBuilder _sql = new StringBuilder();

        private readonly NpgsqlCommand _command;

        public CommandBuilder(NpgsqlCommand command)
        {
            _command = command;
        }

        public void Append(string text)
        {
            _sql.Append(text);
        }

        public void Append(object o)
        {
            _sql.Append(o);
        }

        public void AppendPathToObject(MemberInfo[] members, string column)
        {
            _sql.Append(BuildJsonObjectLocator(column, members));
        }

        public void AppendPathToValue(MemberInfo[] members, string column)
        {
            _sql.Append(BuildJsonStringLocator(column, members));
        }

        public override string ToString()
        {
            return _sql.ToString();
        }

        public void Clear()
        {
            _sql.Clear();
        }

        public void Dispose()
        {
        }

        public void AddParameters(object parameters)
        {
            _command.AddParameters(parameters);
        }

        public NpgsqlParameter AddParameter(object value, NpgsqlDbType? dbType = null)
        {
            return _command.AddParameter(value, dbType);
        }

        public NpgsqlParameter AddJsonParameter(string json)
        {
            return _command.AddParameter(json, NpgsqlDbType.Jsonb);
        }

        public NpgsqlParameter AddNamedParameter(string name, object value)
        {
            return _command.AddNamedParameter(name, value);
        }

        public void UseParameter(NpgsqlParameter parameter)
        {
            var sql = _sql.ToString();
            _sql.Clear();
            _sql.Append(sql.UseParameter(parameter));
        }
    }
}
