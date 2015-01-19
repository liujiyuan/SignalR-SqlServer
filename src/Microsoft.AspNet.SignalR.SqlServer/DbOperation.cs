﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.SignalR.SqlServer
{
    internal class DbOperation
    {
        private List<DbParameter> _parameters = new List<DbParameter>();
        private readonly IDbProviderFactory _dbProviderFactory;

        public DbOperation(string connectionString, string commandText, ILogger logger)
            : this(connectionString, commandText, logger, SqlClientFactory.Instance.AsIDbProviderFactory())
        {
            
        }

        public DbOperation(string connectionString, string commandText, ILogger logger, IDbProviderFactory dbProviderFactory)
        {
            ConnectionString = connectionString;
            CommandText = commandText;
            Logger = logger;
            _dbProviderFactory = dbProviderFactory;
        }

        public DbOperation(string connectionString, string commandText, ILogger logger, params DbParameter[] parameters)
            : this(connectionString, commandText, logger)
        {
            if (parameters != null)
            {
                _parameters.AddRange(parameters);
            }
        }

        public string LoggerPrefix { get; set; }

        public IList<DbParameter> Parameters
        {
            get { return _parameters; }
        }

        protected ILogger Logger { get; private set; }

        protected string ConnectionString { get; private set; }

        protected string CommandText { get; private set; }

        public virtual object ExecuteScalar()
        {
            return Execute(cmd => cmd.ExecuteScalar());
        }

        public virtual int ExecuteNonQuery()
        {
            return Execute(cmd => cmd.ExecuteNonQuery());
        }

        public virtual Task<int> ExecuteNonQueryAsync()
        {
            var tcs = new TaskCompletionSource<int>();
            Execute(cmd => cmd.ExecuteNonQueryAsync(), tcs);
            return tcs.Task;
        }

        public virtual int ExecuteReader(Action<DbDataReader, DbOperation> processRecord)
        {
            return ExecuteReader(processRecord, null);
        }

        protected virtual int ExecuteReader(Action<DbDataReader, DbOperation> processRecord, Action<DbCommand> commandAction)
        {
            return Execute(cmd =>
            {
                if (commandAction != null)
                {
                    commandAction(cmd);
                }

                var reader = cmd.ExecuteReader();
                var count = 0;

                while (reader.Read())
                {
                    count++;
                    processRecord(reader, this);
                }

                return count;
            });
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "It's the caller's responsibility to dispose as the command is returned"),
         SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "General purpose SQL utility command")]
        protected virtual DbCommand CreateCommand(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = CommandText;

            if (Parameters != null && Parameters.Count > 0)
            {
                for (var i = 0; i < Parameters.Count; i++)
                {
                    command.Parameters.Add(Parameters[i].Clone(_dbProviderFactory));
                }
            }

            return command;
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "False positive?")]
        private T Execute<T>(Func<DbCommand, T> commandFunc)
        {
            T result = default(T);
            DbConnection connection = null;
            
            try
            {
                connection = _dbProviderFactory.CreateConnection();
                connection.ConnectionString = ConnectionString;
                var command = CreateCommand(connection);
                connection.Open();
                LoggerCommand(command);
                result = commandFunc(command);
            }
            finally
            {
                if (connection != null)
                {
                    connection.Dispose();
                }
            }

            return result;
        }

        private void LoggerCommand(DbCommand command)
        {
            if (Logger.IsEnabled(LogLevel.Verbose))
            {
                Logger.WriteVerbose(String.Format("Created DbCommand: CommandType={0}, CommandText={1}, Parameters={2}", command.CommandType, command.CommandText,
                    command.Parameters.Cast<DbParameter>()
                        .Aggregate(string.Empty, (msg, p) => string.Format(CultureInfo.InvariantCulture, "{0} [Name={1}, Value={2}]", msg, p.ParameterName, p.Value)))
                );
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "Disposed in async Finally block"),
         SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed in async Finally block")]
        private void Execute<T>(Func<DbCommand, Task<T>> commandFunc, TaskCompletionSource<T> tcs)
        {
            DbConnection connection = null;
           
            try
            {
                connection = _dbProviderFactory.CreateConnection();
                connection.ConnectionString = ConnectionString;
                var command = CreateCommand(connection);

                connection.Open();

                commandFunc(command)
                    .Then(result => tcs.SetResult(result))
                    .Catch(ex => tcs.SetUnwrappedException(ex), Logger)
                    .Finally(state =>
                    {
                        var conn = (DbConnection)state;
                        if (conn != null)
                        {
                            conn.Dispose();
                        }
                    }, connection);
            }
            catch (Exception)
            {
                if (connection != null)
                {
                    connection.Dispose();
                }
                throw;
            }
        }
    }
}
