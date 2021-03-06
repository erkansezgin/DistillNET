﻿/*
 * Copyright © 2017 Jesse Nicholson and CloudVeil Technology, Inc.
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using DistillNET.Extensions;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace DistillNET
{
    /// <summary>
    /// The FilterDbCollection class is responsible for parsing and storing rules, with associated
    /// category ID's, into a database. The class is also responsible for calling up collections of
    /// rules on the fly for any given, known domain. Called up rules are re-parsed at every lookup
    /// rather than serialized/deserialized, because the parser is much faster than such utilities
    /// such as protobuf.
    /// </summary>
    public class FilterDbCollection : IDisposable
    {
        /// <summary>
        /// Our rule parser.
        /// </summary>
        private AbpFormatRuleParser m_ruleParser;

        /// <summary>
        /// Our Sqlite connection.
        /// </summary>
        private SqliteConnection m_connection;

        /// <summary>
        /// The global key used to index non-domain specific filters.
        /// </summary>
        private readonly string m_globalKey;

        private BloomFilter<string> m_whitelistFilter;
        private BloomFilter<string> m_blacklistFilter;

        /// <summary>
        /// Constructs a new FilterDbCollection using an in-memory database.
        /// </summary>
        public FilterDbCollection()
        {

            var version = typeof(FilterDbCollection).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
            var rnd = new Random();
            var rndNum = rnd.Next();
            var generatedDbName = string.Format("{0} {1} - {2}", nameof(FilterDbCollection), version, rndNum);

            // "Data Source = :memory:; Cache = shared;"
            var cb = new SqliteConnectionStringBuilder();
            cb.DataSource = generatedDbName;
            cb.Mode = SqliteOpenMode.Memory;
            cb.Cache = SqliteCacheMode.Shared;
            m_connection = new SqliteConnection(cb.ToString());

            //m_connection. = SQLiteConnectionFlags.UseConnectionPool | SQLiteConnectionFlags.NoConvertSettings | SQLiteConnectionFlags.NoVerifyTypeAffinity;            
            m_connection.Open();

            ConfigureDatabase();

            CreateTables();

            m_globalKey = "global";

            m_ruleParser = new AbpFormatRuleParser();
        }

        /// <summary>
        /// Constructs a new FilterDbCollection.
        /// </summary>
        /// <param name="dbAbsolutePath">
        /// The absolute path where the database exists or should be created.
        /// </param>
        /// <param name="overwrite">
        /// If true, and a file exists at the supplied absolute db path, it will be deleted first.
        /// Default is true.
        /// </param>
        /// <param name="useMemory">
        /// If true, the database will be created as a purely in-memory database.
        /// </param>
        public FilterDbCollection(string dbAbsolutePath, bool overwrite = true, bool useMemory = false)
        {
            if (!useMemory && overwrite && File.Exists(dbAbsolutePath))
            {
                File.Delete(dbAbsolutePath);
            }

            bool isNew = !File.Exists(dbAbsolutePath);

            m_ruleParser = new AbpFormatRuleParser();

            if (useMemory)
            {
                var version = typeof(FilterDbCollection).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
                var rnd = new Random();
                var rndNum = rnd.Next();
                var generatedDbName = string.Format("{0} {1} - {2}", nameof(FilterDbCollection), version, rndNum);

                // "Data Source = :memory:; Cache = shared;"
                var cb = new SqliteConnectionStringBuilder();
                cb.Mode = SqliteOpenMode.Memory;
                cb.Cache = SqliteCacheMode.Shared;
                cb.DataSource = generatedDbName;
                m_connection = new SqliteConnection(cb.ToString());
            }
            else
            {
                // "Data Source={0};"
                var cb = new SqliteConnectionStringBuilder();
                cb.Mode = SqliteOpenMode.ReadWriteCreate;
                cb.Cache = SqliteCacheMode.Shared;
                cb.DataSource = dbAbsolutePath;
                m_connection = new SqliteConnection(cb.ToString());
            }

            //m_connection. = SQLiteConnectionFlags.UseConnectionPool | SQLiteConnectionFlags.NoConvertSettings | SQLiteConnectionFlags.NoVerifyTypeAffinity;            
            m_connection.Open();

            ConfigureDatabase();

            CreateTables();

            m_globalKey = "global";
        }

        /// <summary>
        /// Configures the database page size, cache size for optimal performance according to our
        /// needs.
        /// </summary>
        private async void ConfigureDatabase()
        {
            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA page_size=65536;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA cache_size=-65536;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA soft_heap_limit=131072;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA synchronous=OFF;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA journal_mode=OFF;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA locking_mode=NORMAL;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA temp_store=2;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA ignore_check_constraints=TRUE;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA cell_size_check=FALSE;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA cache_spill=FALSE;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA automatic_index=FALSE;";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA busy_timeout=20000;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Creates needed tables on the source database if they do not exist.
        /// </summary>
        private async void CreateTables()
        {
            using (var tsx = m_connection.BeginTransaction())
            using (var command = m_connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS UrlFiltersIndex (Domains VARCHAR(255), CategoryId INT16, IsWhitelist BOOL, Source TEXT)";
                await command.ExecuteNonQueryAsync();

                tsx.Commit();
            }
        }

        /// <summary>
        /// Creates needed indexes on the database if they do not exist.
        /// </summary>
        private void CreatedIndexes()
        {
            using (var command = m_connection.CreateCommand())
            {
                command.CommandText = "CREATE INDEX IF NOT EXISTS domain_index ON UrlFiltersIndex (Domains)";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Finalize the database for read only access. This presently builds indexes if they do not
        /// exist, and is meant to be called AFTER all bulk insertions are complete. Note that
        /// calling this command does not rebuild the Sqlite connection to enforce read-only mode.
        /// Write access is still possible after calling.
        /// </summary>
        public void FinalizeForRead()
        {
            CreatedIndexes();
        }

        /// <summary>
        /// Parses the supplied list of rules and stores them in the assigned database for retrieval,
        /// indexed by the rule's domain names.
        /// </summary>
        /// <param name="rawRuleStrings">
        /// The raw filter strings.
        /// </param>
        /// <param name="categoryId">
        /// The category ID that each of the supplied filters is deemeded to belong to.
        /// </param>
        /// <returns>
        /// A tuple where the first item is the total number of rules successfully parsed and stored,
        /// and the second item is the total number of rules that failed to be parsed and stored.
        /// Failed rules are an indication of improperly formatted rules.
        /// </returns>
        public async Task<Tuple<int, int>> ParseStoreRules(string[] rawRuleStrings, short categoryId)
        {
            int loaded = 0, failed = 0;

            using (var transaction = m_connection.BeginTransaction())
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO UrlFiltersIndex VALUES ($domain, $categoryId, $isWhitelist, $source)";
                    var domainParam = new SqliteParameter("$domain", DbType.String);
                    var categoryIdParam = new SqliteParameter("$categoryId", DbType.Int16);
                    var isWhitelistParam = new SqliteParameter("$isWhitelist", DbType.Boolean);
                    var sourceParam = new SqliteParameter("$source", DbType.String);
                    cmd.Parameters.Add(domainParam);
                    cmd.Parameters.Add(categoryIdParam);
                    cmd.Parameters.Add(isWhitelistParam);
                    cmd.Parameters.Add(sourceParam);

                    var len = rawRuleStrings.Length;
                    for (int i = 0; i < len; ++i)
                    {
                        rawRuleStrings[i] = rawRuleStrings[i].TrimQuick();
                        var filter = m_ruleParser.ParseAbpFormattedRule(rawRuleStrings[i], categoryId) as UrlFilter;

                        if (filter == null)
                        {
                            ++failed;
                            continue;
                        }

                        ++loaded;

                        if (filter.ApplicableDomains.Count > 0)
                        {
                            foreach (var dmn in filter.ApplicableDomains)
                            {
                                cmd.Parameters[0].Value = dmn;
                                cmd.Parameters[1].Value = categoryId;
                                cmd.Parameters[2].Value = filter.IsException;
                                cmd.Parameters[3].Value = rawRuleStrings[i];
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }
                        else
                        {
                            cmd.Parameters[0].Value = m_globalKey;
                            cmd.Parameters[1].Value = categoryId;
                            cmd.Parameters[2].Value = filter.IsException;
                            cmd.Parameters[3].Value = rawRuleStrings[i];
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                transaction.Commit();
            }

            return new Tuple<int, int>(loaded, failed);
        }

        /// <summary>
        /// Call this once you're done loading all your filter rules.
        /// </summary>
        public async void InitializeBloomFilters()
        {
            int whitelistCount = 0, blacklistCount = 0;

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(1) FROM UrlFiltersIndex WHERE IsWhitelist = $isWhitelist";
                var isWhitelistParam = new SqliteParameter("$isWhitelist", false);
                cmd.Parameters.Add(isWhitelistParam);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    reader.Read();
                    blacklistCount = reader.GetInt32(0);
                }

                cmd.Parameters[0].Value = true;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    reader.Read();
                    whitelistCount = reader.GetInt32(0);
                }
            }

            m_whitelistFilter = new BloomFilter<string>(whitelistCount == 0 ? 100 : whitelistCount);
            m_blacklistFilter = new BloomFilter<string>(blacklistCount == 0 ? 100 : blacklistCount);

            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Domains, IsWhitelist FROM UrlFiltersIndex;";

                using (SqliteDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    while (reader.Read())
                    {
                        string domain = reader.GetString(0);
                        bool isWhitelist = reader.GetBoolean(1);

                        if (isWhitelist)
                        {
                            m_whitelistFilter.Add(domain);
                        }
                        else
                        {
                            m_blacklistFilter.Add(domain);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Parses the supplied list of rules and stores them in the assigned database for retrieval,
        /// indexed by the rule's domain names.
        /// </summary>
        /// <param name="rawRulesStream">
        /// The stream from which to read raw rules as lines.
        /// </param>
        /// <param name="categoryId">
        /// The category ID that each of the supplied filters is deemeded to belong to.
        /// </param>
        /// <returns>
        /// A tuple where the first item is the total number of rules successfully parsed and stored,
        /// and the second item is the total number of rules that failed to be parsed and stored.
        /// Failed rules are an indication of improperly formatted rules.
        /// </returns>
        public async Task<Tuple<int, int>> ParseStoreRulesFromStream(Stream rawRulesStream, short categoryId)
        {
            int loaded = 0, failed = 0;

            using (var transaction = m_connection.BeginTransaction())
            {
                using (var cmd = m_connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO UrlFiltersIndex VALUES ($domain, $categoryId, $isWhitelist, $source)";
                    var domainParam = new SqliteParameter("$domain", DbType.String);
                    var categoryIdParam = new SqliteParameter("$categoryId", DbType.Int16);
                    var isWhitelistParam = new SqliteParameter("$isWhitelist", DbType.Boolean);
                    var sourceParam = new SqliteParameter("$source", DbType.String);
                    cmd.Parameters.Add(domainParam);
                    cmd.Parameters.Add(categoryIdParam);
                    cmd.Parameters.Add(isWhitelistParam);
                    cmd.Parameters.Add(sourceParam);

                    string line = null;
                    using (var sw = new StreamReader(rawRulesStream))
                        while ((line = await sw.ReadLineAsync()) != null)
                        {
                            line = line.TrimQuick();
                            var filter = m_ruleParser.ParseAbpFormattedRule(line, categoryId) as UrlFilter;

                            if (filter == null)
                            {
                                ++failed;
                                continue;
                            }

                            ++loaded;

                            if (filter.ApplicableDomains.Count > 0)
                            {
                                foreach (var dmn in filter.ApplicableDomains)
                                {
                                    cmd.Parameters[0].Value = dmn;
                                    cmd.Parameters[1].Value = categoryId;
                                    cmd.Parameters[2].Value = filter.IsException;
                                    cmd.Parameters[3].Value = line;
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                cmd.Parameters[0].Value = m_globalKey;
                                cmd.Parameters[1].Value = categoryId;
                                cmd.Parameters[2].Value = filter.IsException;
                                cmd.Parameters[3].Value = line;
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }
                }

                transaction.Commit();
            }

            return new Tuple<int, int>(loaded, failed);
        }

        /// <summary>
        /// Gets all blacklisting filters for the supplied domain.
        /// </summary>
        /// <param name="domain">
        /// The domain for which all filters should be loaded. Default is global, meaning that
        /// filters not anchored to any particular domain will be loaded.
        /// </param>
        /// <returns>
        /// A list of all compiled blacklisting URL filters for the given domain.
        /// </returns>
        public async Task<List<UrlFilter>> GetFiltersForDomain(string domain = "global")
        {
            return await GetFiltersForDomain(domain, false);
        }

        /// <summary>
        /// Gets all whitelisting filters for the supplied domain.
        /// </summary>
        /// <param name="domain">
        /// The domain for which all filters should be loaded. Default is global, meaning that
        /// filters not anchored to any particular domain will be loaded.
        /// </param>
        /// <returns>
        /// A list of all compiled whitelisting URL filters for the given domain.
        /// </returns>
        public async Task<List<UrlFilter>> GetWhitelistFiltersForDomain(string domain = "global")
        {
            return await GetFiltersForDomain(domain, true);
        }

        /// <summary>
        /// Uses the bloom filters to determine whether we need to check GetFiltersForDomain().
        /// If this function returns true, we'll need to call GetFiltersForDomain and run CheckIfFiltersApply.
        /// If this function returns false, we can safely assume that the return for GetFiltersForDomain would be an empty array.
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="isWhitelist"></param>
        /// <returns>true if domain most likely has applicable filters, false if it definitely does not.</returns>
        public bool PrefetchIsDomainInList(string domain, bool isWhitelist)
        {
            if (domain == null || m_whitelistFilter == null || m_blacklistFilter == null)
            {
                return true;
            }

            if (isWhitelist && m_whitelistFilter.Contains(domain))
            {
                return true;
            }

            if (!isWhitelist && m_blacklistFilter.Contains(domain))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a list of either all whitelist or all blacklist filters for the given domain.
        /// </summary>
        /// <param name="domain">
        /// The domain for which to retrieve all whitelist or blacklist filters.
        /// </param>
        /// <param name="isWhitelist">
        /// Whether or not to get whitelist filters. If false, blacklist filters will be selected.
        /// </param>
        /// <returns>
        /// A list of either all whitelist or all blacklist filters for the given domain.
        /// </returns>
        private async Task<List<UrlFilter>> GetFiltersForDomain(string domain, bool isWhitelist)
        {
            var retVal = new List<UrlFilter>();

            var allPossibleVariations = GetAllPossibleSubdomains(domain);

            using (var myConn = new SqliteConnection(m_connection.ConnectionString))
            {
                await myConn.OpenAsync();

                using (var tsx = myConn.BeginTransaction())
                using (var cmd = myConn.CreateCommand())
                {
                    switch (isWhitelist)
                    {
                        case true:
                            {
                                cmd.CommandText = @"SELECT * from UrlFiltersIndex where Domains = $domainId AND IsWhitelist = 1";
                            }
                            break;

                        default:
                            {
                                cmd.CommandText = @"SELECT * from UrlFiltersIndex where Domains = $domainId AND IsWhitelist = 0";
                            }
                            break;
                    }

                    var domainParam = new SqliteParameter("$domainId", System.Data.DbType.String);
                    cmd.Parameters.Add(domainParam);

                    foreach (var sub in allPossibleVariations)
                    {
                        cmd.Parameters[0].Value = sub;

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                short catId = reader.GetInt16(1);
                                retVal.Add((UrlFilter)m_ruleParser.ParseAbpFormattedRule(reader.GetString(3), catId));
                            }
                        }
                    }
                    tsx.Commit();
                }
            }

            return retVal;
        }

        private List<string> GetAllPossibleSubdomains(string inputDomain)
        {
            var retVal = new List<string>() { inputDomain };
            int subPos = inputDomain.IndexOfQuick('.');

            while (subPos != -1)
            {
                inputDomain = inputDomain.Substring(subPos + 1);
                retVal.Add(inputDomain);
                subPos = inputDomain.IndexOfQuick('.');
            }

            return retVal;
        }

        public List<Filter> GetFiltersForRequest(Uri requestString, string referer = "")
        {
            return null;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (m_connection != null)
                    {
                        m_connection.Close();
                        m_connection = null;
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~FilterDbCollection() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}