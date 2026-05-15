---
name: sql-test-data-generator-architecture
description: Tài liệu kiến trúc và kỹ thuật cốt lõi của SQL Test Data Generator. Dùng khi cần sửa parser, scenario planner, data generation engine, insert executor, sample/max mode, CSV import-export, UI vận hành, hoặc khi cần điều tra lỗi hệ thống theo tư duy end-to-end.
---

# SQL Test Data Generator - Kiến Trúc Kỹ Thuật

> Rule update: agents must read the repository root `AGENTS.md` before changing parser, scenario planning, data generation, insert execution, script generation, CSV import/export, or UI controls that affect generated data. `AGENTS.md` is the controlling rule file when it is stricter than this architecture note.

## 1. Mục đích của file này

File này đóng vai trò như một tài liệu kỹ thuật nền cho toàn bộ dự án. Mục tiêu không phải mô tả UI hay hướng dẫn người dùng cuối, mà là:

- giải thích hệ thống đang được tổ chức như thế nào;
- chốt các invariant kỹ thuật không được phá;
- ghi lại các kỹ thuật và pattern đã dùng để parser SQL, phân tích scenario, sinh data, insert vào DB, export/import CSV;
- giúp những lần sửa sau không đi lặp lại các lỗi đã từng xảy ra.

Nếu phải sửa dự án theo hướng an toàn, hãy đọc file này trước khi chạm vào source.

## 2. Triết lý thiết kế

Hệ thống này không được phép dừng ở mức:

- `insertable dataset`

Mà phải tiến tới:

- `query-satisfiable dataset`

Nghĩa là dữ liệu sinh ra phải:

1. insert được;
2. không vi phạm PK/FK/unique/type range;
3. thỏa được các điều kiện của câu SQL đang phân tích;
4. join ra đủ dữ liệu để mọi cột trong kết quả có thể lấy được;
5. giữ semantics đúng theo scenario dương/âm;
6. khi có aggregate, cardinality, self-reference hay subquery thì vẫn phải đúng về nghĩa, không chỉ đúng cú pháp.

Đây là khác biệt cốt lõi của dự án này so với generator kiểu random/faker thông thường.

## 3. Các tầng kiến trúc

Hệ thống hiện tại có thể nhìn như 8 tầng.

### 3.1 Parsing Layer

Nhiệm vụ:

- parse SQL Server T-SQL bằng AST thật;
- trích xuất bảng, alias, join, predicate, aggregate, select columns, subquery, CTE, derived query;
- giữ được lineage từ alias/cột biểu diễn về bảng/cột gốc.

Thành phần chính:

- `SqlTestDataGenerator/Parsing/SqlParserService.cs`
- `SqlTestDataGenerator/Parsing/PredicateTreeBuilder.cs`
- `SqlTestDataGenerator/Parsing/PredicateTruthPlanner.cs`
- `SqlTestDataGenerator/Parsing/Visitors/*`
- `SqlTestDataGenerator/Parsing/Models/*`

Kỹ thuật dùng:

- `Microsoft.SqlServer.TransactSql.ScriptDom`
- visitor pattern để lấy table/join/group/subquery/aggregate
- predicate tree thay vì danh sách điều kiện phẳng
- scope-aware parsing cho `WHERE`, `HAVING`, `JOIN ON`, `SubqueryWhere`

### 3.2 Semantic Model Layer

Nhiệm vụ:

- gom toàn bộ kết quả parse vào một mô hình trung gian thống nhất để các tầng sau dùng lại;
- đảm bảo mọi tầng downstream không phải tự parse SQL lại.

Trung tâm là `ParsedQuery`, hiện chứa:

- `Tables`
- `Joins`
- `WhereConditions`
- `HavingConditions`
- `PredicateScopes`
- `GroupByColumns`
- `Subqueries`
- `Aggregates`
- `SelectColumns`
- `DerivedColumnMappings`
- `AliasToTableMap`
- cờ `HasDistinct`, `TopCount`, `Warnings`, `Errors`

Nguyên tắc:

- Parser parse một lần.
- Từ đó về sau, mọi quyết định generate đều phải dựa trên semantic model này.

### 3.3 Scenario Planning Layer

Nhiệm vụ:

- tạo ra các scenario người dùng thấy trên UI;
- giữ đúng boolean semantics của query;
- không tạo thừa scenario;
- không bỏ sót scenario âm quan trọng;
- gắn truth-map rõ ràng cho từng scenario để engine sinh data đúng theo nhánh đó.

Thành phần chính:

- `SqlTestDataGenerator/DataGeneration/BranchCoverageAnalyzer.cs`
- `SqlTestDataGenerator/Parsing/PredicateTruthPlanner.cs`

Pattern chính:

- `Positive scenario`: đường đi dương chuẩn;
- `WhereNegative`, `HavingNegative`, `SubqueryMiss`, `JoinMiss`;
- không dựa trên “mỗi condition = một scenario” kiểu cơ học;
- mà dựa trên minimal falsifying assignment của cây boolean.

Ví dụ:

- với `(A OR B) AND C`, scenario âm đúng không phải là:
  - fail `A`
  - fail `B`
  - fail `C`

Mà phải có ít nhất:

- `A=false` và `B=false`
- hoặc `C=false`

Tức là planner đang làm branch planning theo semantics, không phải theo text order.

### 3.4 Data Synthesis Layer

Nhiệm vụ:

- sinh dữ liệu cho từng scenario;
- thỏa tất cả constraint cần cho query;
- giữ được mode sample hoặc mode max;
- quyết định số row hỗ trợ nội bộ khi query có aggregate/self-join/self-reference.

Trung tâm:

- `SqlTestDataGenerator/DataGeneration/DataGenerationEngine.cs`
- `SqlTestDataGenerator/DataGeneration/ValueGenerators/ValueGenerators.cs`
- `SqlTestDataGenerator/DataGeneration/DependencyOrderResolver.cs`

Đây là tầng phức tạp nhất của dự án.

### 3.5 Schema & Normalization Layer

Nhiệm vụ:

- lấy schema thật từ SQL Server;
- biết kiểu dữ liệu, max length, precision/scale, PK/FK/unique, computed, identity;
- normalize dữ liệu generator sinh ra thành giá trị tương thích SQL Server.

Thành phần:

- `SqlTestDataGenerator/Schema/SchemaIntrospector.cs`
- `SqlTestDataGenerator/Schema/Models/SchemaModels.cs`
- `SqlTestDataGenerator/Schema/SqlServerValueNormalizer.cs`
- `SqlTestDataGenerator/DataGeneration/GeneratedDataSetNormalizer.cs`

### 3.6 Database Execution Layer

Nhiệm vụ:

- clear dữ liệu cũ đúng thứ tự;
- tự synthesize ancestor rows nếu cần;
- insert đúng dependency order;
- handle identity insert;
- validate FK sau insert;
- trả lại thống kê insert thực tế cho UI và export.

Thành phần:

- `SqlTestDataGenerator/Database/GeneratedDataDbExecutor.cs`
- `SqlTestDataGenerator/Database/TableKeySeedResolver.cs`
- `SqlTestDataGenerator/Database/TableSampleExtractor.cs`
- `SqlTestDataGenerator/Database/DatabaseConnectionManager.cs`

### 3.7 Script / CSV Layer

Nhiệm vụ:

- sinh script chạy tay tương đương với `Insert to DB`;
- export/import CSV theo format ổn định;
- không làm mất nghĩa `NULL` vs `empty string`.

Thành phần:

- `SqlTestDataGenerator/Output/ScriptGenerators.cs`
- `SqlTestDataGenerator/Database/TableCsvExporter.cs`
- `SqlTestDataGenerator/Database/TableCsvFolderImporter.cs`

### 3.8 UI / Operational Layer

Nhiệm vụ:

- hiển thị parse result, scenario list, executable SQL script, log vận hành;
- cho người dùng chọn scenario, chọn mode, insert, export/import;
- giữ trạng thái vận hành an toàn, không nhầm dataset cũ với mode mới.

Thành phần:

- `SqlTestDataGenerator/UI/MainForm.cs`
- `SqlTestDataGenerator/UI/ConnectionForm.cs`

## 4. Luồng dữ liệu end-to-end

Đây là pipeline chuẩn từ đầu vào đến đầu ra.

### Bước 1. Người dùng nhập SQL

UI nhận SQL text và gọi `SqlParserService.Parse(...)`.

### Bước 2. Parser dựng AST và semantic model

Parser:

- parse AST bằng ScriptDom;
- lấy `QuerySpecification` chính;
- đi xuyên `CTE`, `derived query`, `subquery`;
- trích xuất table/alias/join/conditions/aggregates;
- dựng `PredicateScope` và predicate tree cho từng scope.

Output của bước này là `ParsedQuery`.

### Bước 3. Analyzer dựng scenarios

`BranchCoverageAnalyzer`:

- lấy predicate tree từ `ParsedQuery.PredicateScopes`;
- dựng truth map dương chuẩn;
- tính minimal false assignments;
- sinh danh sách scenario người dùng thấy trên UI.

Điểm quan trọng:

- scenario là “semantic branch”, không phải “mỗi dòng WHERE”.

### Bước 4. Lấy schema thật từ DB

Nếu đang connected:

- `SchemaIntrospector` đọc metadata thật;
- `TableKeySeedResolver` lấy next key theo từng bảng;
- `TableSampleExtractor` lấy sample rows thật.

Nếu offline:

- engine fallback sang inferred/minimal schema khi có thể.

### Bước 5. Engine sinh dữ liệu theo scenario

`DataGenerationEngine`:

- gom generation scope:
  - main tables
  - subquery tables
  - FK ancestor closure
- resolve insert order;
- áp truth map của scenario;
- sinh dữ liệu cho từng row, từng bảng;
- đảm bảo FK/PK/join/literal/aggregate/self-reference hoạt động.

### Bước 6. Normalize dữ liệu

`GeneratedDataSetNormalizer` + `SqlServerValueNormalizer`:

- clamp kiểu số;
- chuẩn hóa ngày giờ;
- chuẩn hóa kiểu SQL Server;
- xử lý `NULL` / `empty string`.

### Bước 7. Tạo script và/hoặc insert trực tiếp

Hai đường ra chính:

- script:
  - `CleanupScriptGenerator`
  - `InsertScriptGenerator`
- insert trực tiếp:
  - `GeneratedDataDbExecutor`

### Bước 8. Export / Import CSV

- export:
  - đọc lại từ DB, không export từ text preview;
  - format tương thích DBeaver;
- import:
  - đọc toàn bộ folder CSV;
  - parse thành `GeneratedDataSet`;
  - insert lại qua executor chuẩn.

## 5. Kỹ thuật parser đang dùng

### 5.1 AST-based parsing

Hệ thống không parse bằng regex toàn cục.

Lý do:

- SQL Server có cú pháp phong phú;
- alias, derived query, nested subquery, CTE, window function làm regex dễ sai;
- cần giữ scope thật của predicate.

### 5.2 Predicate Tree thay vì flat conditions

`PredicateTreeBuilder` tạo cây gồm:

- `PredicateBinaryExpression`
- `PredicateNotExpression`
- `PredicateLeafExpression`

Mỗi leaf giữ `ConditionInfo` ổn định với:

- `Key`
- `ScopeId`
- `ScopeLabel`
- `TableAlias`
- `ColumnName`
- `ReferencedColumns`
- `LeftExpression`
- `RightExpression`
- `AggregateFunc`
- `ExpressionText`
- cờ `HasSubquery`, `IsSubqueryPredicate`

Lợi ích:

- tránh mất semantics của `OR`, `NOT`, `(...)`;
- scenario planner và generator cùng dùng chung truth model;
- dễ truy vết lỗi scope leakage.

### 5.3 Alias lineage và derived column mapping

Parser giữ:

- `AliasToTableMap`
- `DerivedColumnMappings`

Mục tiêu:

- điều kiện nằm ở query ngoài vẫn map được về cột gốc ở query trong;
- không để mất nguồn gốc cột khi qua CTE/derived table.

### 5.4 Function-aware condition modeling

Với các predicate không còn là `column op literal` thuần, engine dùng:

- `LeftExpression`
- `RightExpression`
- `ExpressionText`
- `ReferencedColumns`

Đây là cơ sở cho việc xử lý các biểu thức như:

- `ISNULL(A, '') + ISNULL(B, '') = N'間'`
- `LEN(p.ProductName) >= 5`
- `CHARINDEX('a', LOWER(p.ProductName)) > 0`
- `LEFT(...) <> RIGHT(...)`
- `SUBSTRING(...) <> 'xx'`
- `TRIM(...) = ...`
- `EXISTS` với `STRING_SPLIT(...)`

## 6. Kỹ thuật scenario planning đang dùng

### 6.1 Canonical positive path

Mỗi query luôn có một positive path chuẩn. Tất cả những gì bắt buộc để query ra row sẽ được gom vào truth map dương.

### 6.2 Minimal falsifying assignments

Negative scenario không sinh theo cảm tính.

Planner dùng:

- `PredicateTruthPlanner.GetMinimalAssignments(root, false)`

Để tìm tập assignment tối thiểu làm scope fail.

Lợi ích:

- không thừa scenario;
- không thiếu nhánh phủ;
- không hiểu sai `OR` thành nhiều scenario âm độc lập.

### 6.3 Subquery-aware scenarios

`EXISTS`, `NOT EXISTS`, `IN`, `NOT IN` được model như predicate scope riêng. Analyzer phải hiểu polarity thật của chúng.

### 6.4 LEFT JOIN miss

`LEFT JOIN miss` chỉ hợp lệ khi:

- right alias thực sự có thể vắng mặt;
- không bị predicate ngoài ép trở thành inner semantics.

Nếu không, không được sinh scenario kiểu miss.

## 7. Kỹ thuật data generation đang dùng

### 7.1 Query-satisfiable generation

Nguyên tắc số 1:

- mục tiêu không phải “sinh row hợp schema”
- mà là “sinh row làm query chạy đúng theo scenario”

Nghĩa là mọi cột dùng trong:

- `WHERE`
- `HAVING`
- `JOIN ON`
- subquery predicate
- expression predicate

đều có độ ưu tiên cao hơn:

- sample imitation
- max-mode
- fallback randomization

### 7.2 Generation scope closure

Engine không chỉ sinh các bảng xuất hiện trực tiếp trong query. Nó còn phải gom:

- subquery tables;
- ancestor tables theo FK;
- các bảng cần để self-reference chain chạy được;
- các bảng cần cho witness rows của aggregate/self-join pattern.

### 7.3 Dependency-aware insert order

`DependencyOrderResolver` sắp thứ tự theo:

- FK graph
- join graph
- fallback order an toàn

### 7.4 Self-reference planning

Cho các bảng như:

- `Categories.ParentCategoryID`
- `Employees.ManagerEmployeeID`

engine dùng self-reference plan để:

- xác định chiều sâu tối thiểu;
- ép số row tối thiểu đủ chain;
- tạo self-FK thành chain thật.

### 7.5 Pair-pattern / self-join synthesis

Một số query không thể thỏa chỉ bằng row độc lập.

Ví dụ:

- self-join trên `OrderItems` để tìm cặp sản phẩm mua cùng nhau.

Engine phải hiểu pattern:

- cùng `OrderID`
- hai `ProductID` khác nhau
- cùng cặp lặp lại trên nhiều order

Đây là pattern synthesis, không phải random row-by-row.

### 7.6 Aggregate-aware row planning

Aggregate không thể xử lý như scalar predicate.

Engine tách:

- `COUNT` semantics = cardinality của tập row đóng góp;
- `SUM/AVG` semantics = giá trị measure + số row đóng góp;
- group cardinality = số output groups, không đồng nghĩa số row vật lý.

Điểm này dẫn đến các khái niệm:

- support rows nội bộ;
- result cardinality anchor tables;
- single-result preference khi `Rows/Table = 1`.

### 7.7 Sample-based mode

Khi không bật `Maxlength/MaxValue`, generator đi theo:

- sample row thật từ DB;
- mutate dựa trên sample;
- tránh học lại dữ liệu synthetic do tool vừa insert.

Kỹ thuật hỗ trợ:

- `baseline sample cache`
- lọc `sample bẩn` như `TestData_*`
- `column fingerprint`
- `row fingerprint`

### 7.8 Maxlength/MaxValue mode

Khi bật checkbox:

- string tăng lên gần/đúng `MaxLength`;
- numeric tăng lên vùng `high-safe`, không dùng type-max mù quáng;
- row sau giảm dần so với row trước.

Nguyên tắc:

- max-mode phải tôn trọng query semantics;
- không được phá `CAST`, `SUM`, `AVG`, `*`, `/`, `money`, `decimal`, `smallint`, `tinyint`, v.v.

### 7.9 Function-aware expression solving

Các function không thể bị xem như text thường.

Engine hiện đã đi theo hướng:

- nhận diện function call trong expression;
- giữ literal và referenced columns riêng;
- solve theo semantics của biểu thức.

Nhóm đặc biệt quan trọng:

- string functions
- null-handling functions
- date functions
- cast/convert functions
- aggregate/window-related expressions

## 8. Các invariant không được phá

Đây là các luật cứng.

### 8.1 Literal dominance

Nếu SQL chứa literal như:

- `col = 'abc'`
- `col = 123`
- `LEN(col) >= 5`
- `ISNULL(A,'') + ISNULL(B,'') = N'間'`

thì generator phải ưu tiên literal/query condition trước mọi mode khác.

### 8.2 Insertable không đủ

Dữ liệu insert được nhưng query không ra row vẫn bị coi là fail.

### 8.3 Alias-correct targeting

Không được map predicate vào cột khác chỉ vì trùng tên như:

- `IsActive`
- `Code`
- `Status`

### 8.4 Joinable dataset

Nếu query trả cột từ bảng join, dataset phải thật sự join ra được dữ liệu đó.

### 8.5 Type-range safety

Mọi numeric path phải tôn trọng range thật của:

- `tinyint`
- `smallint`
- `int`
- `bigint`
- `decimal(p,s)`
- `numeric(p,s)`
- `money`
- `smallmoney`

### 8.6 NULL khác empty string

Toàn hệ thống phải giữ đúng:

- `NULL`
- `""`

cho cả generate, script, CSV export/import.

## 9. Phân biệt hai mode dữ liệu

### 9.1 Sample mode

Dùng khi checkbox `Maxlength/MaxValue` tắt.

Mục tiêu:

- dữ liệu giống dữ liệu thật hơn;
- vẫn khác nhau giữa cột và giữa record;
- vẫn thỏa query.

### 9.2 Max mode

Dùng khi checkbox bật.

Mục tiêu:

- test biên kiểu dữ liệu;
- string dài;
- số lớn;
- nhưng vẫn không được làm query overflow hoặc ra rỗng nếu positive path đáng ra phải có row.

## 10. Script / CSV / DB execution

### 10.1 Script generator

Script preview phải là script thực thi được, không phải chỉ là preview INSERT.

Nó phải gồm:

- cleanup đúng thứ tự;
- insert;
- transaction;
- `TRY/CATCH`;
- identity insert khi cần.

### 10.2 Direct DB executor

Executor phải:

- build insert plan hoàn chỉnh;
- synthesize parent rows còn thiếu;
- clear dependent tables nếu cần;
- heal FK trước insert;
- validate FK sau insert trước khi commit.

### 10.3 CSV export/import

Quy ước hiện tại:

- format tương thích DBeaver;
- mỗi bảng một file;
- tên file `schema.table.csv`;
- export đọc từ DB thực;
- import parse ngược lại thành `GeneratedDataSet`.

Semantics bắt buộc:

- `NULL -> ,,`
- `empty string -> ,""`

## 11. Harness và regression strategy

Project có harness riêng:

- `SqlTestDataGenerator.Harness`

Không được sửa lớn ở parser/generator/executor mà không thêm regression tương ứng.

Nhóm regression quan trọng hiện có:

- `exists` / `not exists`
- nested subquery
- CTE / recursive CTE
- self-reference hierarchy
- aggregate satisfaction
- scenario exactness
- sample mode
- max mode
- smallint safety
- pair self-join
- string expression support

Nguyên tắc:

- mỗi bug kiến trúc mới phải đi kèm regression hoặc ít nhất guard rõ ràng;
- nếu fix không khóa được bằng harness thì fix đó chưa đủ tin cậy.

## 12. Phạm vi hỗ trợ hiện tại và giới hạn

### 12.1 Hỗ trợ mạnh

Nhóm `SELECT-family`:

- `SELECT`
- `CTE`
- derived query
- nested subquery
- `JOIN`
- `GROUP BY`
- `HAVING`
- aggregate
- window function
- expression predicate

### 12.2 Chưa phải mục tiêu chính

Các nhóm dưới đây không nên coi là fully supported nếu chưa có regression riêng:

- `MERGE`
- temp table
- table variable
- dynamic SQL
- batch procedural nhiều statement
- DML phức tạp ngoài `SELECT`-family
- JSON/XML transformation sâu

Không có nghĩa parser không đọc được gì, nhưng không được hứa semantics đầy đủ.

## 13. Checklist khi sửa hệ thống

Trước khi sửa:

1. Xác định bug nằm ở tầng nào:
   - parse
   - alias lineage
   - scenario planning
   - generation
   - normalization
   - insert/DB
   - CSV/script/UI
2. Xác định invariant nào đang bị phá.
3. Viết regression hoặc ít nhất tái hiện bug trong harness.

Khi sửa:

1. Không vá ở UI nếu bug nằm ở parser/generator.
2. Không vá bằng special-case cho đúng một câu SQL nếu pattern kỹ thuật còn chung hơn.
3. Luôn ưu tiên fix theo:
   - scope
   - semantics
   - model
   - planner
   - execution

Sau khi sửa:

1. chạy build Release;
2. chạy harness `sanity`;
3. cập nhật `ERROR_AUDIT_SUMMARY.md`;
4. nếu bug mở ra một pattern mới, phải cập nhật chính file này.

## 14. Checklist điều tra bug

Nếu query không ra data:

1. kiểm tra parser đã thấy đủ tables/joins/scopes chưa;
2. kiểm tra scenario truth map có đúng không;
3. kiểm tra literal/query condition có đang thắng sample/max mode không;
4. kiểm tra aggregate cardinality có đúng không;
5. kiểm tra join graph có thật sự nối được đủ bảng không;
6. kiểm tra executor có synthesize đủ parent rows không;
7. kiểm tra query đang fail ở `insertability` hay `query satisfiability`.

Nếu insert lỗi:

1. xác định lỗi là type range, FK, PK, unique, identity hay computed column;
2. kiểm tra đường đi generate có đi qua range/normalize guard không;
3. kiểm tra schema introspection có đúng `precision/scale/max length` không;
4. kiểm tra executor có đang chữa sai chỗ thay vì chặn sớm không.

## 15. Nguyên tắc mở rộng về sau

Nếu muốn mở rộng support cho function hoặc syntax mới, lộ trình đúng là:

1. nhận diện syntax ở parser;
2. biểu diễn lại thành semantic model rõ ràng;
3. cho planner biết nó ảnh hưởng scenario thế nào;
4. cho generator biết solve giá trị ra sao;
5. khóa bằng harness.

Không được làm theo thứ tự ngược lại.

Ví dụ sai:

- thêm special-case trong generator cho một câu SQL cụ thể.

Ví dụ đúng:

- thêm model `ScalarExpressionInfo`/condition handling cho cả một họ function;
- sau đó mới solve mọi query dùng họ function đó.

## 16. Tóm tắt một câu

Toàn bộ dự án này nên được hiểu là:

> một hệ thống chuyển đổi `SQL text -> semantic model -> branch scenarios -> query-satisfiable dataset -> script/DB/CSV outputs`, với trọng tâm là giữ đúng semantics của query thay vì chỉ sinh dữ liệu giả ngẫu nhiên.
