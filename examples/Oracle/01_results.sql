SELECT TO_NUMBER('12345678901234567890123456789012345678') AS exact_amount,
       '通用 Oracle 导出自检' AS sample_text,
       TO_CLOB('长文本字段') AS clob_value, CAST(NULL AS VARCHAR2(10)) AS null_value
FROM dual;
SELECT 1 AS empty_column FROM dual WHERE 1=0;
