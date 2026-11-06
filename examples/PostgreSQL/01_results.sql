SELECT 1234567890123456789012345678901234567890.12345678901234567890::numeric AS exact_amount,
       9007199254740993::bigint AS exact_integer,
       $$通用 PostgreSQL 导出自检;美元引号$$ AS sample_text,
       ARRAY[1,2,3] AS sample_array, NULL::text AS null_value;
SELECT 1 AS empty_column WHERE false;
