namespace ProductionDashboard.Infrastructure.Persistence.Migrations;

/// <summary>
/// 所有迁移按版本号顺序列出。<see cref="DatabaseInitializer"/> 会跳过已应用的版本。
/// </summary>
public static class MigrationCatalog
{
    /// <summary>所有已知迁移,按版本号升序排列。新迁移追加到末尾,不要插队 / 重排。</summary>
    public static IReadOnlyList<Migration> All { get; } = new Migration[]
    {
        new(
            Version: 1,
            Description: "Initial schema · ShiftDefinition / Team / ProductionEvent / ProductionMinute / ShiftSummary / ShiftSlotAssignment / AppConfig / AuditLog + 默认 A/B 班次与A/B 班组",
            Statements: new[]
            {
                // ── 1. 班次定义(版本化) ─────────────────────────────────
                """
                CREATE TABLE ShiftDefinition (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    Code            TEXT    NOT NULL,
                    Name            TEXT    NOT NULL,
                    StartTime       TEXT    NOT NULL,
                    EndTime         TEXT    NOT NULL,
                    CrossesMidnight INTEGER NOT NULL,
                    TargetCount     INTEGER NOT NULL,
                    ColorHex        TEXT    NOT NULL,
                    DisplayOrder    INTEGER NOT NULL,
                    EffectiveFrom   TEXT    NOT NULL,
                    EffectiveTo     TEXT,
                    CreatedAt       TEXT    NOT NULL
                );
                """,
                "CREATE INDEX IX_ShiftDef_Effective ON ShiftDefinition(EffectiveFrom, EffectiveTo);",

                // ── 2. 班组(承载真实生产数据归属) ─────────────────────
                """
                CREATE TABLE Team (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name            TEXT    NOT NULL,
                    LeaderName      TEXT    NOT NULL,
                    Members         TEXT,
                    ColorHex        TEXT    NOT NULL,
                    DisplayOrder    INTEGER NOT NULL,
                    IsActive        INTEGER NOT NULL DEFAULT 1,
                    CreatedAt       TEXT    NOT NULL
                );
                """,
                "CREATE INDEX IX_Team_Active ON Team(IsActive, DisplayOrder);",

                // ── 3. 原始事件 ──────────────────────────────────────────
                """
                CREATE TABLE ProductionEvent (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp   TEXT    NOT NULL,
                    ShiftDate   TEXT    NOT NULL,
                    ShiftDefId  INTEGER NOT NULL,
                    TeamId      INTEGER,
                    Source      TEXT    NOT NULL DEFAULT 'PLC',
                    FOREIGN KEY (ShiftDefId) REFERENCES ShiftDefinition(Id),
                    FOREIGN KEY (TeamId)     REFERENCES Team(Id)
                );
                """,
                "CREATE INDEX IX_Event_ShiftDate ON ProductionEvent(ShiftDate, ShiftDefId);",
                "CREATE INDEX IX_Event_Timestamp ON ProductionEvent(Timestamp);",
                "CREATE INDEX IX_Event_Team ON ProductionEvent(TeamId, ShiftDate);",

                // ── 4. 分钟聚合 ──────────────────────────────────────────
                """
                CREATE TABLE ProductionMinute (
                    MinuteStart TEXT    NOT NULL PRIMARY KEY,
                    ShiftDate   TEXT    NOT NULL,
                    ShiftDefId  INTEGER NOT NULL,
                    TeamId      INTEGER,
                    Count       INTEGER NOT NULL,
                    FOREIGN KEY (ShiftDefId) REFERENCES ShiftDefinition(Id),
                    FOREIGN KEY (TeamId)     REFERENCES Team(Id)
                );
                """,
                "CREATE INDEX IX_Minute_ShiftDate ON ProductionMinute(ShiftDate, ShiftDefId);",
                "CREATE INDEX IX_Minute_Team ON ProductionMinute(TeamId, ShiftDate);",

                // ── 5. 班次汇总(PK 含 TeamId,支持倒班后一个槽位周期内多班组各占一段) ──
                """
                CREATE TABLE ShiftSummary (
                    ShiftDate     TEXT    NOT NULL,
                    ShiftDefId    INTEGER NOT NULL,
                    TeamId        INTEGER NOT NULL,
                    ShiftName     TEXT    NOT NULL,
                    StartTime     TEXT    NOT NULL,
                    EndTime       TEXT,
                    TargetCount   INTEGER NOT NULL,
                    ActualCount   INTEGER NOT NULL DEFAULT 0,
                    PeakHourCount INTEGER,
                    Remark        TEXT,
                    PRIMARY KEY (ShiftDate, ShiftDefId, TeamId),
                    FOREIGN KEY (ShiftDefId) REFERENCES ShiftDefinition(Id),
                    FOREIGN KEY (TeamId)     REFERENCES Team(Id)
                );
                """,
                "CREATE INDEX IX_Summary_Team ON ShiftSummary(TeamId, ShiftDate);",
                "CREATE INDEX IX_Summary_Date ON ShiftSummary(ShiftDate);",

                // ── 6. 槽位 ↔ 班组当前归属 ──────────────────────────────
                """
                CREATE TABLE ShiftSlotAssignment (
                    ShiftDefId          INTEGER NOT NULL PRIMARY KEY,
                    TeamId              INTEGER NOT NULL,
                    AssignedAt          TEXT    NOT NULL,
                    AssignedByOperator  TEXT,
                    FOREIGN KEY (ShiftDefId) REFERENCES ShiftDefinition(Id),
                    FOREIGN KEY (TeamId)     REFERENCES Team(Id)
                );
                """,
                "CREATE INDEX IX_Assign_Team ON ShiftSlotAssignment(TeamId);",

                // ── 7. 配置 / 审计 ───────────────────────────────────────
                """
                CREATE TABLE AppConfig (
                    Key       TEXT NOT NULL PRIMARY KEY,
                    Value     TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """,
                """
                CREATE TABLE AuditLog (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    Category  TEXT NOT NULL,
                    Detail    TEXT NOT NULL,
                    User      TEXT
                );
                """,
                "CREATE INDEX IX_Audit_Time ON AuditLog(Timestamp);",

                // ── 8. 默认 A 班 / B 班(用户可在班次配置页面修改) ──────
                """
                INSERT INTO ShiftDefinition
                    (Code, Name, StartTime, EndTime, CrossesMidnight, TargetCount, ColorHex, DisplayOrder, EffectiveFrom, EffectiveTo, CreatedAt)
                VALUES
                    ('A', '白班', '08:00', '20:00', 0, 1500, '#ff8c42', 1, '1970-01-01', NULL, datetime('now')),
                    ('B', '夜班', '20:00', '08:00', 1, 1200, '#4a9eff', 2, '1970-01-01', NULL, datetime('now'));
                """,

                // ── 9. 默认 A 班 / B 班(班长姓名是占位,管理员可在 UI 改) ──
                //   注:这里的 A班/B班 是**班组**(人员组),会倒班;
                //   不要和 ShiftDefinition.Code='A'/'B'(那是**班次代号**,A=白班 B=夜班) 混淆。
                //   当前刚部署时巧合是「A 班组在 A 班次(白班)」,倒班后会错开。
                """
                INSERT INTO Team (Name, LeaderName, Members, ColorHex, DisplayOrder, IsActive, CreatedAt)
                VALUES
                    ('A 班', '张班长', NULL, '#FF8C42', 1, 1, datetime('now')),
                    ('B 班', '李班长', NULL, '#4A9EFF', 2, 1, datetime('now'));
                """,

                // ── 10. 初始分配:白班槽位(Id=1) → A 班(Id=1) · 夜班槽位(Id=2) → B 班(Id=2) ──
                //   依赖前面两组种子的 AUTOINCREMENT 顺序(空表起步,Id 从 1 开始),
                //   同事务内插入保证 ID 稳定。倒班后由 UI 触发 ShiftSlotAssignment 重映射。
                """
                INSERT INTO ShiftSlotAssignment (ShiftDefId, TeamId, AssignedAt, AssignedByOperator)
                VALUES
                    (1, 1, datetime('now'), 'seed'),
                    (2, 2, datetime('now'), 'seed');
                """,
            }),

        new(
            Version: 2,
            Description: "性能索引 · IX_Event_Timestamp_Team(覆盖按班组的时间窗查询:启动复盘 / 班次切换 reload)",
            Statements: new[]
            {
                // CountEventsByTeamInRangeAsync / CountEventsForTeamInRangeAsync 走 Timestamp 范围 + TeamId 过滤,
                // 没有该索引时只能用 IX_Event_Timestamp 范围扫描后逐行回表取 TeamId。这个复合(部分)索引让 TeamId 被覆盖,
                // 避免回表。部分索引(WHERE TeamId IS NOT NULL)更小,且这些查询都只关心有班组归属的行。
                "CREATE INDEX IF NOT EXISTS IX_Event_Timestamp_Team ON ProductionEvent(Timestamp, TeamId) WHERE TeamId IS NOT NULL;",
            }),

        new(
            Version: 3,
            Description: "分钟聚合补全 · 用原始事件重算 ProductionMinute,补上旧版定时聚合漏掉的分钟(本周 / 本月 / 本年累计偏少)",
            Statements: new[]
            {
                MinuteAggregateSql.RebuildAll,
            }),
    };
}
