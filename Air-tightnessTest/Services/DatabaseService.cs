// Services/DatabaseService.cs
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using LumbarMassageTest.Models;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using LumbarMassageTest.UserControls;

namespace LumbarMassageTest.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly ILogService _logService;
        private static User _currentUser;

        public static User CurrentUser
        {
            get { return _currentUser; }
            set { _currentUser = value; }
        }

        public DatabaseService(ILogService? logService = null)
        {
            _logService = logService ?? LogService.Instance;
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData.db");
            _connectionString = $"Data Source={dbPath}";

            if (!File.Exists(dbPath))
            {
                File.WriteAllBytes(dbPath, new byte[0]);
                InitializeDatabase();
            }
            else
            {
                InitializeDatabase();
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                // 创建测试记录表
                string createTestRecordTable = @"
                    CREATE TABLE IF NOT EXISTS TestRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        TestTime TEXT NOT NULL,
                        WorkOrder TEXT,
                        ProductModel TEXT NOT NULL,
                        ProductCode TEXT NOT NULL,
                        Operator TEXT NOT NULL,
                        Channel INTEGER NOT NULL,
                        TestCount INTEGER NOT NULL,
                        TestVoltage REAL NOT NULL,
                        StaticCurrent REAL NOT NULL,
                        LumbarCurrents TEXT,
                        MassageCurrents TEXT,
                        AirLeakResults TEXT,
                        Result INTEGER NOT NULL,
                        FailReason TEXT,
                        TestDuration TEXT
                    )";

                // 创建用户表
                string createUserTable = @"
                    CREATE TABLE IF NOT EXISTS Users (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Username TEXT NOT NULL UNIQUE,
                        FullName TEXT,
                        PasswordHash TEXT NOT NULL,
                        Role TEXT NOT NULL,
                        Department TEXT,
                        Email TEXT,
                        IsActive BOOLEAN NOT NULL DEFAULT 1,
                        CreatedAt TEXT NOT NULL,
                        LastLoginAt TEXT,
                        LoginCount INTEGER DEFAULT 0
                    )";

                // 创建登录历史表
                string createLoginHistoryTable = @"
                    CREATE TABLE IF NOT EXISTS LoginHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Username TEXT NOT NULL,
                        LoginTime TEXT NOT NULL,
                        IPAddress TEXT,
                        Success BOOLEAN NOT NULL
                    )";

                string createMessageConfigTable = @"
                    CREATE TABLE IF NOT EXISTS MessageConfigs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ModelName TEXT NOT NULL,
                        Channel INTEGER NOT NULL,
                        MessageType TEXT NOT NULL,
                        Data TEXT NOT NULL,
                        UNIQUE(ModelName, Channel, MessageType)
                    )";

                using var cmd1 = new SqliteCommand(createTestRecordTable, connection);
                using var cmd2 = new SqliteCommand(createUserTable, connection);
                using var cmd3 = new SqliteCommand(createLoginHistoryTable, connection);
                using var cmd4 = new SqliteCommand(createMessageConfigTable, connection);

                cmd1.ExecuteNonQuery();
                cmd2.ExecuteNonQuery();
                cmd3.ExecuteNonQuery();
                cmd4.ExecuteNonQuery();

                // 确保数据库结构包含最新字段
                EnsureTestRecordsSchema(connection);

                // 规范化历史测试记录中的产品条码，移除异常字符
                NormalizeHistoricalProductCodes(connection);

                // 插入默认管理员账户
                InsertDefaultAdmin(connection);
            }
            catch (Exception ex)
            {
                _logService.LogError("初始化数据库失败", ex);
            }
        }

        private void NormalizeHistoricalProductCodes(SqliteConnection connection)
        {
            try
            {
                using var selectCmd = new SqliteCommand("SELECT Id, ProductCode FROM TestRecords", connection);
                using var reader = selectCmd.ExecuteReader();

                var pendingUpdates = new List<(long Id, string Sanitized)>();

                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    string rawCode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    string sanitizedCode = CodeScanService.SanitizeBarcode(rawCode);

                    if (!string.Equals(rawCode, sanitizedCode, StringComparison.Ordinal))
                    {
                        pendingUpdates.Add((id, sanitizedCode));
                    }
                }

                foreach (var update in pendingUpdates)
                {
                    using var updateCmd = new SqliteCommand("UPDATE TestRecords SET ProductCode = @ProductCode WHERE Id = @Id", connection);
                    updateCmd.Parameters.AddWithValue("@ProductCode", update.Sanitized);
                    updateCmd.Parameters.AddWithValue("@Id", update.Id);
                    updateCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("标准化历史条码失败", ex);
            }
        }

        private void EnsureTestRecordsSchema(SqliteConnection connection)
        {
            try
            {
                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var pragmaCmd = new SqliteCommand("PRAGMA table_info(TestRecords);", connection))
                using (var reader = pragmaCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existingColumns.Add(reader.GetString(1));
                    }
                }

                void AddColumnIfMissing(string columnName, string columnDefinition)
                {
                    if (!existingColumns.Contains(columnName))
                    {
                        using var alterCmd = new SqliteCommand($"ALTER TABLE TestRecords ADD COLUMN {columnName} {columnDefinition};", connection);
                        alterCmd.ExecuteNonQuery();
                    }
                }

                AddColumnIfMissing("LumbarCurrents", "TEXT DEFAULT ''");
                AddColumnIfMissing("MassageCurrents", "TEXT DEFAULT ''");
                AddColumnIfMissing("AirLeakResults", "TEXT DEFAULT ''");
                AddColumnIfMissing("TestDuration", "REAL DEFAULT 0");
            }
            catch (Exception ex)
            {
                _logService.LogError("更新数据库结构失败", ex);
            }
        }

        private void InsertDefaultAdmin(SqliteConnection connection)
        {
            string checkAdmin = "SELECT COUNT(*) FROM Users WHERE Username = 'admin'";
            using var checkCmd = new SqliteCommand(checkAdmin, connection);
            var count = Convert.ToInt32(checkCmd.ExecuteScalar());

            if (count == 0)
            {
                string passwordHash = GetPasswordHash("123456");

                string insertAdmin = @"
                INSERT INTO Users (Username, FullName, PasswordHash, Role, Department, Email, IsActive, CreatedAt, LoginCount)
                VALUES ('admin', 'Administrator', @PasswordHash, 'Admin', 'IT', 'admin@example.com', 1, @CreatedAt, 0)";

                using var insertCmd = new SqliteCommand(insertAdmin, connection);
                insertCmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
                insertCmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                insertCmd.ExecuteNonQuery();
            }
        }

        #region 密码哈希方法
        public string GetPasswordHash(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                return Convert.ToBase64String(hashedBytes);
            }
        }
        #endregion

        #region 测试记录相关方法
        public async Task<bool> SaveTestRecordAsync(TestRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string sql = @"
                    INSERT INTO TestRecords
                    (TestTime, WorkOrder, ProductModel, ProductCode, Operator, Channel, TestCount,
                     TestVoltage, StaticCurrent, LumbarCurrents, MassageCurrents, AirLeakResults, Result, FailReason, TestDuration)
                    VALUES
                    (@TestTime, @WorkOrder, @ProductModel, @ProductCode, @Operator, @Channel, @TestCount,
                     @TestVoltage, @StaticCurrent, @LumbarCurrents, @MassageCurrents, @AirLeakResults, @Result, @FailReason, @TestDuration)";

                using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@TestTime", record.TestTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@WorkOrder", record.WorkOrder ?? string.Empty);
                cmd.Parameters.AddWithValue("@ProductModel", record.ProductModel ?? string.Empty);
                cmd.Parameters.AddWithValue("@ProductCode", SanitizeProductCode(record.ProductCode));
                cmd.Parameters.AddWithValue("@Operator", ResolveOperator(record.Operator));
                cmd.Parameters.AddWithValue("@Channel", record.Channel);
                cmd.Parameters.AddWithValue("@TestCount", record.TestCount);
                cmd.Parameters.AddWithValue("@TestVoltage", record.TestVoltage);
                cmd.Parameters.AddWithValue("@StaticCurrent", record.StaticCurrent ?? 0);
                cmd.Parameters.AddWithValue("@LumbarCurrents", SerializeCurrents(record.LumbarCurrents));
                cmd.Parameters.AddWithValue("@MassageCurrents", SerializeCurrents(record.MassageCurrents));
                cmd.Parameters.AddWithValue("@AirLeakResults", JsonConvert.SerializeObject(record.AirLeakResults ?? new List<AirLeakPressureResult>()));
                cmd.Parameters.AddWithValue("@Result", (int)record.Result);
                cmd.Parameters.AddWithValue("@FailReason", record.FailReason ?? string.Empty);
                cmd.Parameters.AddWithValue("@TestDuration", record.TestDuration);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("保存测试记录失败", ex);
                return false;
            }
        }

        private static string SerializeCurrents(IReadOnlyCollection<double> currents)
        {
            if (currents == null || currents.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(",", currents.Select(c => c.ToString(CultureInfo.InvariantCulture)));
        }

        private static string SanitizeProductCode(string? value)
        {
            return CodeScanService.SanitizeBarcode(value);
        }

        private static string ResolveOperator(string @operator)
        {
            if (!string.IsNullOrWhiteSpace(@operator))
            {
                return @operator;
            }

            return CurrentUser?.Username ?? string.Empty;
        }

        public async Task<List<TestRecord>> GetTestRecordsAsync(DateTime startTime, DateTime endTime, string productModel = null)
        {
            var records = new List<TestRecord>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string sql = @"
                    SELECT * FROM TestRecords 
                    WHERE TestTime >= @StartTime AND TestTime <= @EndTime";

                if (!string.IsNullOrEmpty(productModel))
                {
                    sql += " AND ProductModel = @ProductModel";
                }

                sql += " ORDER BY TestTime DESC";

                using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@StartTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@EndTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));

                if (!string.IsNullOrEmpty(productModel))
                {
                    cmd.Parameters.AddWithValue("@ProductModel", productModel);
                }

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(MapTestRecord(reader));
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("获取测试记录失败", ex);
            }

            return records;
        }

        // 获取操作员列表
        public async Task<List<string>> GetOperatorsAsync()
        {
            var operators = new List<string>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = new SqliteCommand("SELECT DISTINCT Operator FROM TestRecords WHERE Operator IS NOT NULL ORDER BY Operator", connection);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                operators.Add(reader.GetString(0));
            }

            return operators;
        }

        // 获取测试统计信息
        public async Task<TestStatistics> GetTestStatisticsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);

            var sql = $@"
            SELECT
                COUNT(CASE WHEN Result IN (2, 3) THEN 1 END) as TotalCount,
                COUNT(CASE WHEN Result = 2 THEN 1 END) as PassCount,
                COUNT(CASE WHEN Result = 3 THEN 1 END) as FailCount,
                AVG(TestDuration) as AvgTestDuration
            FROM TestRecords 
            {whereClause}";

            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new TestStatistics
                {
                    TotalCount = reader.GetInt32(0),
                    PassCount = reader.GetInt32(1),
                    FailCount = reader.GetInt32(2),
                    AvgTestDuration = reader.IsDBNull(3) ? 0 : reader.GetDouble(3)
                };
            }

            return new TestStatistics();
        }

        // 获取测试记录数量
        public async Task<int> GetTestRecordCountAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);

            var sql = $"SELECT COUNT(*) FROM TestRecords {whereClause}";
            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);

            var result_count = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result_count);
        }

        // 获取分页测试记录
        public async Task<List<TestRecord>> GetTestRecordsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode, int page, int pageSize)
        {
            var records = new List<TestRecord>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);

            var sql = $@"
            SELECT * FROM TestRecords 
            {whereClause}
            ORDER BY TestTime DESC 
            LIMIT @PageSize OFFSET @Offset";

            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("@PageSize", pageSize);
            command.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);

            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                records.Add(MapTestRecord(reader));
            }
            return records;
        }

        // 获取所有测试记录(用于导出)
        public async Task<List<TestRecord>> GetAllTestRecordsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode)
        {
            var records = new List<TestRecord>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var sql = $@"
            SELECT * FROM TestRecords
            {whereClause}
            ORDER BY TestTime DESC";
            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                records.Add(MapTestRecord(reader));
            }
            return records;
        }

        public async Task<TestRecord?> GetLatestRecordByProductCodeAsync(string productCode)
        {
            var sanitizedCode = CodeScanService.SanitizeBarcode(productCode);

            if (string.IsNullOrWhiteSpace(sanitizedCode))
            {
                return null;
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT * FROM TestRecords
                WHERE ProductCode = @ProductCode
                ORDER BY TestTime DESC
                LIMIT 1";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@ProductCode", sanitizedCode);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapTestRecord(reader);
            }

            return null;
        }

        public async Task<int> CountTestRecordsByProductCodeAsync(string productCode)
        {
            var sanitizedCode = CodeScanService.SanitizeBarcode(productCode);

            if (string.IsNullOrWhiteSpace(sanitizedCode))
            {
                return 0;
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT COUNT(*) FROM TestRecords WHERE ProductCode = @ProductCode";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@ProductCode", sanitizedCode);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result ?? 0);
        }

        private TestRecord MapTestRecord(SqliteDataReader reader)
        {
            var record = new TestRecord
            {
                Id = TryGetInt(reader, "Id") ?? 0,
                TestTime = TryGetDateTime(reader, "TestTime") ?? DateTime.MinValue,
                WorkOrder = reader["WorkOrder"]?.ToString() ?? string.Empty,
                ProductModel = reader["ProductModel"]?.ToString() ?? string.Empty,
                ProductCode = reader["ProductCode"]?.ToString() ?? string.Empty,
                Operator = reader["Operator"]?.ToString() ?? string.Empty,
                Channel = TryGetInt(reader, "Channel") ?? 0,
                TestCount = TryGetInt(reader, "TestCount") ?? 0,
                TestVoltage = TryGetDouble(reader, "TestVoltage") ?? 0,
                StaticCurrent = TryGetDouble(reader, "StaticCurrent"),
                Result = (TestResult)(TryGetInt(reader, "Result") ?? 0),
                FailReason = reader["FailReason"]?.ToString() ?? string.Empty,
                TestDuration = TryGetDouble(reader, "TestDuration") ?? 0
            };

            var lumbarCurrents = ParseCurrents(reader["LumbarCurrents"]?.ToString());
            var massageCurrents = ParseCurrents(reader["MassageCurrents"]?.ToString());

            record.LumbarCurrents = lumbarCurrents;
            record.MassageCurrents = massageCurrents;

            (record.LumbarAverageCurrent, record.LumbarMaxCurrent) = CalculateCurrentMetrics(lumbarCurrents);
            (record.MassageAverageCurrent, record.MassageMaxCurrent) = CalculateCurrentMetrics(massageCurrents);

            return record;
        }

        private static List<double> ParseCurrents(string currents)
        {
            if (string.IsNullOrWhiteSpace(currents))
            {
                return new List<double>();
            }

            var values = new List<double>();
            var segments = currents.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (double.TryParse(segment, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    values.Add(value);
                }
                else if (double.TryParse(segment, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static (double? average, double? max) CalculateCurrentMetrics(List<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return (null, null);
            }

            var filtered = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
            if (filtered.Count == 0)
            {
                return (null, null);
            }

            return (filtered.Average(), filtered.Max());
        }

        private static int? TryGetInt(SqliteDataReader reader, string column)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (reader.IsDBNull(ordinal)) return null;
                return reader.GetInt32(ordinal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        private static double? TryGetDouble(SqliteDataReader reader, string column)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (reader.IsDBNull(ordinal)) return null;
                return reader.GetDouble(ordinal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                var value = reader[column]?.ToString();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                {
                    return parsed;
                }
                return null;
            }
        }

        private static DateTime? TryGetDateTime(SqliteDataReader reader, string column)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (reader.IsDBNull(ordinal)) return null;
                return reader.GetDateTime(ordinal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                var value = reader[column]?.ToString();
                if (DateTime.TryParse(value, out var parsed))
                {
                    return parsed;
                }
                return null;
            }
        }

        // 按日期统计
        public async Task<List<DateStatistic>> GetDateStatisticsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode)
        {
            var statistics = new List<DateStatistic>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var sql = $@"
            SELECT
                DATE(TestTime) as TestDate,
                COUNT(CASE WHEN Result IN (2, 3) THEN 1 END) as TotalCount,
                COUNT(CASE WHEN Result = 2 THEN 1 END) as PassCount,
                COUNT(CASE WHEN Result = 3 THEN 1 END) as FailCount
            FROM TestRecords 
            {whereClause}
            GROUP BY DATE(TestTime)
            ORDER BY TestDate DESC";
            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                statistics.Add(new DateStatistic
                {
                    Date = DateTime.Parse(reader.GetString(0)),
                    TotalCount = reader.GetInt32(1),
                    PassCount = reader.GetInt32(2),
                    FailCount = reader.GetInt32(3)
                });
            }
            return statistics;
        }

        // 按产品型号统计
        public async Task<List<ModelStatistic>> GetModelStatisticsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string result, string workOrder, string productCode)
        {
            var statistics = new List<ModelStatistic>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, result, workOrder, productCode);
            var sql = $@"
            SELECT
                ProductModel,
                COUNT(CASE WHEN Result IN (2, 3) THEN 1 END) as TotalCount,
                COUNT(CASE WHEN Result = 2 THEN 1 END) as PassCount,
                COUNT(CASE WHEN Result = 3 THEN 1 END) as FailCount
            FROM TestRecords 
            {whereClause}
            GROUP BY ProductModel
            ORDER BY TotalCount DESC";
            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                statistics.Add(new ModelStatistic
                {
                    ProductModel = reader.GetString(0),
                    TotalCount = reader.GetInt32(1),
                    PassCount = reader.GetInt32(2),
                    FailCount = reader.GetInt32(3)
                });
            }
            return statistics;
        }

        // 失效原因统计
        public async Task<List<FailReasonStatistic>> GetFailReasonStatisticsAsync(DateTime startDate, DateTime endDate,
            string model, string operatorName, string workOrder, string productCode)
        {
            var statistics = new List<FailReasonStatistic>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var whereClause = BuildWhereClause(startDate, endDate, model, operatorName, "Fail", workOrder, productCode);
            var parameters = BuildParameters(startDate, endDate, model, operatorName, "Fail", workOrder, productCode);

            var sql = $@"
            SELECT 
                FailReason,
                COUNT(*) as Count
            FROM TestRecords 
            {whereClause}
            AND FailReason IS NOT NULL AND FailReason != ''
            GROUP BY FailReason
            ORDER BY Count DESC";

            var command = new SqliteCommand(sql, connection);
            AddParameters(command, parameters);

            using var reader = await command.ExecuteReaderAsync();

            var totalFailCount = 0;
            var reasons = new List<(string reason, int count)>();

            while (await reader.ReadAsync())
            {
                var reason = reader.GetString(0);
                var count = reader.GetInt32(1);
                reasons.Add((reason, count));
                totalFailCount += count;
            }

            // 计算百分比
            foreach (var (reason, count) in reasons)
            {
                statistics.Add(new FailReasonStatistic
                {
                    FailReason = reason,
                    Count = count,
                    Percentage = totalFailCount > 0 ? (double)count / totalFailCount : 0
                });
            }

            return statistics;
        }
        #endregion

        #region 用户管理相关方法
        public async Task<User> ValidateUserAsync(string username, string password)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string sql = "SELECT * FROM Users WHERE Username = @Username AND IsActive = 1";
                using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@Username", username);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    // 验证密码哈希
                    string storedHash = reader["PasswordHash"].ToString();
                    string inputHash = GetPasswordHash(password);

                    if (storedHash == inputHash)
                    {
                        return new User
                        {
                            Id = Convert.ToInt32(reader["Id"]),
                            Username = reader["Username"].ToString(),
                            FullName = reader["FullName"].ToString(),
                            PasswordHash = reader["PasswordHash"].ToString(),
                            Role = reader["Role"].ToString(),
                            Department = reader["Department"].ToString(),
                            Email = reader["Email"].ToString(),
                            IsActive = Convert.ToBoolean(reader["IsActive"]),
                            CreatedAt = DateTime.Parse(reader["CreatedAt"].ToString()),
                            LastLoginAt = reader["LastLoginAt"] is DBNull ?
                                null : (DateTime?)DateTime.Parse(reader["LastLoginAt"].ToString()),
                            LoginCount = Convert.ToInt32(reader["LoginCount"])
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("用户验证失败", ex);
            }

            return null;
        }

        public async Task<bool> SaveUserAsync(User user)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string sql = @"
                    UPDATE Users 
                    SET LastLoginAt = @LastLoginAt, 
                        LoginCount = @LoginCount
                    WHERE Id = @Id";

                using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@LastLoginAt",
                    user.LastLoginAt.HasValue ?
                    user.LastLoginAt.Value.ToString("yyyy-MM-dd HH:mm:ss") :
                    (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@LoginCount", user.LoginCount);
                cmd.Parameters.AddWithValue("@Id", user.Id);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("保存用户失败", ex);
                return false;
            }
        }

        // 获取所有用户
        public async Task<List<User>> GetAllUsersAsync()
        {
            var users = new List<User>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // 修复SQL查询，使用正确的排序字段
                string sql = "SELECT * FROM Users ORDER BY CreatedAt DESC";
                using var cmd = new SqliteCommand(sql, connection);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    users.Add(new User
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        Username = reader["Username"].ToString(),
                        FullName = reader["FullName"].ToString(),
                        PasswordHash = reader["PasswordHash"].ToString(),
                        Role = reader["Role"].ToString(),
                        Department = reader["Department"] is DBNull ? null : reader["Department"].ToString(),
                        Email = reader["Email"] is DBNull ? null : reader["Email"].ToString(),
                        IsActive = Convert.ToBoolean(reader["IsActive"]),
                        CreatedAt = DateTime.Parse(reader["CreatedAt"].ToString()),
                        LastLoginAt = reader["LastLoginAt"] is DBNull ?
                            null : (DateTime?)DateTime.Parse(reader["LastLoginAt"].ToString()),
                        LoginCount = Convert.ToInt32(reader["LoginCount"])
                    });
                }
            }
            catch (Exception ex)
            {
                _logService.LogError($"获取用户列表失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                throw new Exception("获取用户列表失败，请检查数据库连接和表结构", ex);
            }

            return users;
        }

        // 创建用户
        public async Task<bool> CreateUserAsync(User user)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // 检查用户名是否已存在
                var checkCommand = new SqliteCommand("SELECT COUNT(*) FROM Users WHERE Username = @Username", connection);
                checkCommand.Parameters.AddWithValue("@Username", user.Username);
                var existCount = Convert.ToInt32(await checkCommand.ExecuteScalarAsync());

                if (existCount > 0)
                {
                    throw new Exception("用户名已存在");
                }

                // 如果没有设置密码，使用默认密码
                if (string.IsNullOrEmpty(user.PasswordHash))
                {
                    user.PasswordHash = GetPasswordHash("123456");
                }

                var command = new SqliteCommand(@"
                INSERT INTO Users (Username, FullName, PasswordHash, Role, Department, Email, IsActive, CreatedAt, LoginCount)
                VALUES (@Username, @FullName, @PasswordHash, @Role, @Department, @Email, @IsActive, @CreatedAt, 0)", connection);

                command.Parameters.AddWithValue("@Username", user.Username);
                command.Parameters.AddWithValue("@FullName", user.FullName);
                command.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
                command.Parameters.AddWithValue("@Role", user.Role);
                command.Parameters.AddWithValue("@Department", user.Department ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Email", user.Email ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@IsActive", user.IsActive);
                command.Parameters.AddWithValue("@CreatedAt", user.CreatedAt);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logService.LogError("创建用户失败", ex);
                throw;
            }
        }

        // 更新用户
        public async Task<bool> UpdateUserAsync(User user)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                UPDATE Users 
                SET FullName = @FullName, Role = @Role, Department = @Department, 
                    Email = @Email, IsActive = @IsActive";

                var parameters = new List<SqliteParameter>
                {
                    new SqliteParameter("@FullName", user.FullName),
                    new SqliteParameter("@Role", user.Role),
                    new SqliteParameter("@Department", user.Department ?? (object)DBNull.Value),
                    new SqliteParameter("@Email", user.Email ?? (object)DBNull.Value),
                    new SqliteParameter("@IsActive", user.IsActive),
                    new SqliteParameter("@Id", user.Id)
                };

                // 如果提供了新密码，则更新密码
                if (!string.IsNullOrEmpty(user.PasswordHash))
                {
                    sql += ", PasswordHash = @PasswordHash";
                    parameters.Add(new SqliteParameter("@PasswordHash", user.PasswordHash));
                }

                sql += " WHERE Id = @Id";

                var command = new SqliteCommand(sql, connection);
                command.Parameters.AddRange(parameters.ToArray());

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logService.LogError("更新用户失败", ex);
                throw;
            }
        }

        // 删除用户
        public async Task<bool> DeleteUserAsync(int userId)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // 检查是否有关联的测试记录
                var checkCommand = new SqliteCommand(@"
                SELECT COUNT(*) FROM TestRecords 
                WHERE Operator = (SELECT Username FROM Users WHERE Id = @UserId)", connection);

                    checkCommand.Parameters.AddWithValue("@UserId", userId);
                var recordCount = Convert.ToInt32(await checkCommand.ExecuteScalarAsync());

                if (recordCount > 0)
                {
                    throw new Exception($"无法删除用户，该用户有 {recordCount} 条测试记录");
                }

                var command = new SqliteCommand("DELETE FROM Users WHERE Id = @UserId", connection);
                command.Parameters.AddWithValue("@UserId", userId);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logService.LogError("删除用户失败", ex);
                throw;
            }
        }

        // 重置密码
        public async Task<bool> ResetPasswordAsync(int userId, string newPassword)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var passwordHash = GetPasswordHash(newPassword);

                var command = new SqliteCommand("UPDATE Users SET PasswordHash = @PasswordHash WHERE Id = @UserId", connection);
                command.Parameters.AddWithValue("@PasswordHash", passwordHash);
                command.Parameters.AddWithValue("@UserId", userId);

                var result = await command.ExecuteNonQueryAsync();
                return result > 0;
            }
            catch (Exception ex)
            {
                _logService.LogError("重置密码失败", ex);
                throw;
            }
        }

        // 根据ID获取用户
        public async Task<User?> GetUserByIdAsync(int userId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = new SqliteCommand(@"
            SELECT Id, Username, FullName, PasswordHash, Role, Department, Email, 
                   IsActive, CreatedAt, LastLoginAt, LoginCount 
            FROM Users 
            WHERE Id = @UserId", connection);
            command.Parameters.AddWithValue("@UserId", userId);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new User
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    Username = reader.GetString(reader.GetOrdinal("Username")),
                    FullName = reader.GetString(reader.GetOrdinal("FullName")),
                    PasswordHash = reader.GetString(reader.GetOrdinal("PasswordHash")),
                    Role = reader.GetString(reader.GetOrdinal("Role")),
                    Department = reader.IsDBNull(reader.GetOrdinal("Department")) ? null : reader.GetString(reader.GetOrdinal("Department")),
                    Email = reader.IsDBNull(reader.GetOrdinal("Email")) ? null : reader.GetString(reader.GetOrdinal("Email")),
                    IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                    LastLoginAt = reader.IsDBNull(reader.GetOrdinal("LastLoginAt")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("LastLoginAt")),
                    LoginCount = reader.GetInt32(reader.GetOrdinal("LoginCount"))
                };
            }

            return null;
        }

        // 更新登录信息
        public async Task UpdateLoginInfoAsync(string username)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var command = new SqliteCommand(@"
                UPDATE Users 
                SET LastLoginAt = @LastLoginAt, LoginCount = LoginCount + 1 
                WHERE Username = @Username", connection);

                command.Parameters.AddWithValue("@LastLoginAt", DateTime.Now);
                command.Parameters.AddWithValue("@Username", username);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError("更新登录信息失败", ex);
            }
        }

        // 获取活跃用户数量
        public async Task<int> GetActiveUserCountAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = new SqliteCommand("SELECT COUNT(*) FROM Users WHERE IsActive = 1", connection);
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        // 获取用户统计信息
        public async Task<UserStatistics> GetUserStatisticsAsync(string username)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 获取登录次数
            var loginCommand = new SqliteCommand("SELECT COUNT(*) FROM LoginHistory WHERE Username = @Username", connection);
            loginCommand.Parameters.AddWithValue("@Username", username);
            var loginCount = await loginCommand.ExecuteScalarAsync() ?? 0;

            // 获取测试次数
            var testCommand = new SqliteCommand("SELECT COUNT(*) FROM TestRecords WHERE Operator = @Operator", connection);
            testCommand.Parameters.AddWithValue("@Operator", username);
            var testCount = await testCommand.ExecuteScalarAsync() ?? 0;

            return new UserStatistics
            {
                LoginCount = Convert.ToInt32(loginCount),
                TestCount = Convert.ToInt32(testCount)
            };
        }

        // 检查用户权限
        public bool HasPermission(string permission)
        {
            if (CurrentUser == null) return false;

            // 管理员拥有所有权限
            if (CurrentUser.Role == "Admin") return true;

            // 根据角色和权限名称进行判断
            return permission switch
            {
                "Test" => CurrentUser.Role == "Engineer" || CurrentUser.Role == "Operator",
                "Manual" => CurrentUser.Role == "Engineer",
                "Config" => CurrentUser.Role == "Engineer",
                "Report" => CurrentUser.Role == "Engineer" || CurrentUser.Role == "Operator",
                "UserManage" => false, // 只有管理员才能管理用户
                "SystemSettings" => false, // 只有管理员才能修改系统设置
                _ => false
            };
        }
        #endregion

        #region 报文配置相关方法
        public async Task SaveMessageConfigAsync(string modelName, int channel, MessageConfig messageConfig)
        {
            if (string.IsNullOrWhiteSpace(modelName) || messageConfig == null)
            {
                return;
            }

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();

                async Task SaveSingleAsync(string messageType, ushort[] data)
                {
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT INTO MessageConfigs (ModelName, Channel, MessageType, Data)
                        VALUES (@ModelName, @Channel, @MessageType, @Data)
                        ON CONFLICT(ModelName, Channel, MessageType)
                        DO UPDATE SET Data = excluded.Data";

                    command.Parameters.AddWithValue("@ModelName", modelName);
                    command.Parameters.AddWithValue("@Channel", channel);
                    command.Parameters.AddWithValue("@MessageType", messageType);
                    command.Parameters.AddWithValue("@Data", SerializeMessageData(data));

                    await command.ExecuteNonQueryAsync();
                }

                await SaveSingleAsync("PowerOn", messageConfig.PowerOnMessage);
                await SaveSingleAsync("Sleep", messageConfig.SleepMessage);
                await SaveSingleAsync("Stop", messageConfig.StopMessage);
                await SaveSingleAsync("Massage", messageConfig.MassageMessage);
                await SaveSingleAsync("Massage2", messageConfig.MassageMessage2);
                await SaveSingleAsync("Read", messageConfig.ReadMessage);

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError("保存报文配置失败", ex);
            }
        }

        public async Task<MessageConfig> LoadChannelMessageConfigAsync(string modelName, int channel)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return null;
            }

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT MessageType, Data FROM MessageConfigs WHERE ModelName = @ModelName AND Channel = @Channel";
                command.Parameters.AddWithValue("@ModelName", modelName);
                command.Parameters.AddWithValue("@Channel", channel);

                var config = new MessageConfig();
                var hasData = false;

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var messageType = reader.GetString(0);
                    var dataString = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var data = DeserializeMessageData(dataString);

                    switch (messageType)
                    {
                        case "PowerOn":
                            config.PowerOnMessage = data;
                            break;
                        case "Sleep":
                            config.SleepMessage = data;
                            break;
                        case "Stop":
                            config.StopMessage = data;
                            break;
                        case "Massage":
                            config.MassageMessage = data;
                            break;
                        case "Massage2":
                            config.MassageMessage2 = data;
                            break;
                        case "Read":
                            config.ReadMessage = data;
                            break;
                    }

                    hasData = true;
                }

                return hasData ? config : null;
            }
            catch (Exception ex)
            {
                _logService.LogError("加载报文配置失败", ex);
                return null;
            }
        }

        public async Task DeleteMessageConfigsAsync(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return;
            }

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM MessageConfigs WHERE ModelName = @ModelName";
                command.Parameters.AddWithValue("@ModelName", modelName);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError("删除报文配置失败", ex);
            }
        }

        private static string SerializeMessageData(ushort[] data)
        {
            var normalized = NormalizeMessageData(data);
            return string.Join(",", normalized.Select(v => v.ToString("X4")));
        }

        private static ushort[] DeserializeMessageData(string data)
        {
            var result = new ushort[20];

            if (string.IsNullOrWhiteSpace(data))
            {
                return result;
            }

            var parts = data.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(parts.Length, result.Length); i++)
            {
                if (ushort.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                {
                    result[i] = value;
                }
            }

            return result;
        }

        private static ushort[] NormalizeMessageData(ushort[] data)
        {
            var normalized = new ushort[20];
            if (data != null)
            {
                for (int i = 0; i < Math.Min(data.Length, normalized.Length); i++)
                {
                    normalized[i] = data[i];
                }
            }
            return normalized;
        }
        #endregion

        #region 辅助方法
        // 辅助方法：构建WHERE子句
        private string BuildWhereClause(DateTime startDate, DateTime endDate, string model, string operatorName, string result, string workOrder, string productCode)
        {
            var conditions = new List<string>
            {
                "TestTime >= @StartDate",
                "TestTime <= @EndDate"
            };

            if (!string.IsNullOrEmpty(model))
                conditions.Add("ProductModel = @ProductModel");

            if (!string.IsNullOrEmpty(operatorName))
                conditions.Add("Operator = @Operator");

            if (TryGetResultValue(result, out _))
                conditions.Add("Result = @Result");

            if (!string.IsNullOrEmpty(workOrder))
                conditions.Add("WorkOrder LIKE @WorkOrder");

            if (!string.IsNullOrEmpty(productCode))
                conditions.Add("ProductCode LIKE @ProductCode");

            return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        }

        // 辅助方法：构建参数字典
        private Dictionary<string, object> BuildParameters(DateTime startDate, DateTime endDate, string model, string operatorName, string result, string workOrder, string productCode)
        {
            var parameters = new Dictionary<string, object>
            {
                { "@StartDate", startDate.Date },
                { "@EndDate", endDate.Date.AddDays(1).AddSeconds(-1) }
            };

            if (!string.IsNullOrEmpty(model))
                parameters.Add("@ProductModel", model);

            if (!string.IsNullOrEmpty(operatorName))
                parameters.Add("@Operator", operatorName);

            if (TryGetResultValue(result, out var parsedResult))
                parameters.Add("@Result", (int)parsedResult);

            if (!string.IsNullOrEmpty(workOrder))
                parameters.Add("@WorkOrder", $"%{workOrder}%");

            if (!string.IsNullOrEmpty(productCode))
                parameters.Add("@ProductCode", $"%{productCode}%");

            return parameters;
        }

        private static bool TryGetResultValue(string result, out TestResult parsedResult)
        {
            if (!string.IsNullOrWhiteSpace(result) && Enum.TryParse(result, true, out parsedResult))
            {
                return parsedResult is TestResult.Pass or TestResult.Fail;
            }

            parsedResult = default;
            return false;
        }

        // 辅助方法：添加参数到命令
        private void AddParameters(SqliteCommand command, Dictionary<string, object> parameters)
        {
            foreach (var param in parameters)
            {
                command.Parameters.AddWithValue(param.Key, param.Value);
            }
        }
        #endregion
    }
}
