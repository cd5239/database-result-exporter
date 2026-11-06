SELECT CAST(12345678901234567890.123456789012345678 AS decimal(38,18)) AS exact_amount,
       CAST(9007199254740993 AS bigint) AS exact_integer,
       N'通用 SQL Server 导出自检' AS sample_text,
       CAST(NULL AS nvarchar(10)) AS null_value;
GO
SELECT CAST(1 AS int) AS empty_column WHERE 1=0;
PRINT N'通用信息消息';
