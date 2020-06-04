﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using Wynnyo.PartitioningTable.Db;
using Wynnyo.PartitioningTable.Entities;

namespace Wynnyo.PartitioningTable.Services
{
    public class DbService
    {
        public readonly DbContext _dbContext;
        public DbService()
        {
            _dbContext = new DbContext();
        }

        /// <summary>
        /// 初始化 Log 表
        /// </summary>
        public void InitDbTable()
        {
            _dbContext.Db.CodeFirst.InitTables<LogEntity>();
        }


        /// <summary>
        /// 初始化分区并绑定表
        /// </summary>
        public void InitPartitioningTables()
        {

            // 循环建立 Consts.ReserveDay / Consts.TaskDay + Consts.ReservePartitions 个分区和分区文件
            var sql = new StringBuilder();
            var partitions = Consts.ReserveDay / Consts.TaskDay;
            var today = DateTime.Today;

            var dateList = new List<string>();
            var tableNameList = new List<string>();

            for (int i = partitions + Consts.ReservePartitions + 1; i > 0 ; i--)
            {
                var dayStr = today.AddDays(1 + Consts.ReservePartitions - i).ToString("yyyyMMdd");
                // 第一个分区为 索引分区,用来以后合并分区
                if (i == partitions + Consts.ReservePartitions + 1)
                {
                    dayStr = "00010101";
                }
                else
                {
                    dateList.Add(dayStr);
                }

                var tableName = Consts.TableName + dayStr;
                var fileName = Consts.FileName + dayStr;

                tableNameList.Add(tableName);

                sql.Append($"ALTER DATABASE {Consts.DbName} ADD FILEGROUP {tableName};");
                sql.Append($@"ALTER DATABASE {Consts.DbName}   
                        ADD FILE   
                        (  
                            NAME = {fileName},  
                            FILENAME = '{Path.Combine(Consts.FilePath, fileName + ".ndf")}',  
                            SIZE = {Consts.FileSize}MB,  
                            MAXSIZE = {Consts.FileMaxSize}MB,  
                            FILEGROWTH = 5MB  
                        )
                        TO FILEGROUP {tableName};");
            }

            // 创建 分区函数
            sql.Append($@"CREATE PARTITION FUNCTION {Consts.PartitionFunctionName}(DATETIME)
                        AS RANGE RIGHT FOR VALUES
                        (
                           '{string.Join("','", dateList)}'
                        )");

            // 创建分区方案
            sql.Append($@"CREATE PARTITION SCHEME {Consts.PartitionSchemeName}
                            AS PARTITION [{Consts.PartitionFunctionName}]
                            TO ({string.Join(",", tableNameList)});");


            // 为 Log 表绑定 分区方案, 创建前需要删除其聚众索引,重新创建主键非聚集索引
            sql.Append($@"ALTER TABLE {Consts.TableName} DROP CONSTRAINT PK_{Consts.TableName}_Id;
                        ALTER TABLE {Consts.TableName}
                        ADD CONSTRAINT PK_{Consts.TableName}_Id PRIMARY KEY NONCLUSTERED (Id ASC)");
            sql.Append($@"CREATE CLUSTERED INDEX IX_CreateTime ON {Consts.TableName} (CreateTime)
                        ON {Consts.PartitionSchemeName} (CreateTime)");

            _dbContext.Db.Ado.ExecuteCommand(sql.ToString());

        }

        /// <summary>
        /// 每天定时任务, 动态操作分区, 动态 修改 分区函数, 动态修改分区方案
        /// </summary>
        public void PartitioningTablesTask(int addDay = 0)
        {
            // 查询数据库文件组的信息
            var dt = _dbContext.Db.Ado.GetDataTable("SELECT f.[name][filegroup] FROM sys.filegroups f");
            var list = dt.AsEnumerable()
                //.Where(e => !string.IsNullOrWhiteSpace(e["name"]?.ToString()) &&
                //            e["name"].ToString().StartsWith("Consts.FileName"))
                .Select(e => e["filegroup"].ToString()?.Replace(Consts.TableName, ""))
                .ToList();

            var sql = new StringBuilder();

            // 为了测试,直接跑明天的任务
            var date = DateTime.Today.AddDays(addDay);

            // 新增 文件组
            for (int i = 1; i <= Consts.ReservePartitions; i++)
            {
                var dateStr = date.AddDays(i).ToString("yyyyMMdd");
                // 数据库中文件组 不存在
                if (!list.Contains(dateStr))
                {
                    var tableName = Consts.TableName + dateStr;
                    var fileName = Consts.FileName + dateStr;

                    sql.Append($"ALTER DATABASE {Consts.DbName} ADD FILEGROUP {tableName};");
                    sql.Append($@"ALTER DATABASE {Consts.DbName}   
                        ADD FILE   
                        (  
                            NAME = {fileName},  
                            FILENAME = '{Path.Combine(Consts.FilePath, fileName + ".ndf")}',  
                            SIZE = {Consts.FileSize}MB,  
                            MAXSIZE = {Consts.FileMaxSize}MB,  
                            FILEGROWTH = 5MB  
                        )
                        TO FILEGROUP {tableName};");

                    // 新增分区函数和分区方案
                    sql.Append($"ALTER PARTITION SCHEME {Consts.PartitionSchemeName} NEXT USED {tableName}; ");
                    sql.Append($"ALTER PARTITION FUNCTION {Consts.PartitionFunctionName} () SPLIT RANGE('{dateStr}'); ");

                }
            }

            if (!string.IsNullOrWhiteSpace(sql.ToString()))
            {
                _dbContext.Db.Ado.ExecuteCommand(sql.ToString());
            }

            sql.Clear();
            // 删除以前的文件组

            var deleteDate = date.AddDays(0 - Consts.ReserveDay / Consts.TaskDay).ToString("yyyyMMdd");
            // 数据库中文件组 存在
            if (list.Contains(deleteDate))
            {
                var fileName = Consts.FileName + deleteDate;
                var tableName = Consts.TableName + deleteDate;

                sql.Append($@"ALTER DATABASE {Consts.DbName} REMOVE FILE {fileName};");

                // 合并分区
                sql.Append($"ALTER PARTITION FUNCTION {Consts.PartitionFunctionName} () MERGE RANGE('{deleteDate}');");

                sql.Append($@"ALTER DATABASE {Consts.DbName} REMOVE FILEGROUP {tableName};");
            }

            if (!string.IsNullOrWhiteSpace(sql.ToString()))
            {
                _dbContext.Db.Ado.ExecuteCommand(sql.ToString());
            }
        }

        /// <summary>
        /// 统计文件和文件组信息
        /// </summary>
        /// <returns></returns>
        public DataTable GetPartitioningFilesInfo()
        {
            return _dbContext.Db.Ado.GetDataTable(@"SELECT df.[name], df.physical_name, f.[name][filegroup]
                            FROM sys.database_files df JOIN sys.filegroups f ON df.data_space_id = f.data_space_id");

        }


        /// <summary>
        /// 统计分区信息
        /// </summary>
        /// <returns></returns>
        public DataTable GetPartitioningTablesInfo()
        {
            var sql = $@"SELECT PARTITION = $PARTITION.{Consts.PartitionSchemeName} (createtime),
                               ROWS      = COUNT(*),
                               MinVal    = MIN(createtime),
                               MaxVal    = MAX(createtime)
                        FROM [dbo].[Log]
                        GROUP BY $PARTITION.{Consts.PartitionSchemeName} (createtime)
                        ORDER BY PARTITION";

            return _dbContext.Db.Ado.GetDataTable(sql);
        }

    }
}
