/*
    Run this file first in DBeaver while connected to master.
    After it succeeds, switch the active database to EnterpriseRetailDemo
    and run EnterpriseRetailDemo.SchemaAndSeed.DBeaver.sql.
*/

USE master;

IF DB_ID(N'EnterpriseRetailDemo') IS NOT NULL
BEGIN
    THROW 51000, N'Database EnterpriseRetailDemo already exists. Drop it manually or rename it in this file before rerunning.', 1;
END;

CREATE DATABASE EnterpriseRetailDemo;

SELECT
    N'EnterpriseRetailDemo database created successfully. Switch DBeaver to this database, then run EnterpriseRetailDemo.SchemaAndSeed.DBeaver.sql.' AS Message;
