# Tổng Hợp Lỗi Và Cách Fix

Phạm vi: tổng hợp toàn bộ các lỗi, lỗ hổng kiến trúc và rủi ro đã phát hiện trong quá trình phát triển SQL Test Data Generator.

Quy ước:
- File này dùng tiếng Việt có dấu.
- Encoding chuẩn cần giữ là `UTF-8 BOM`.
- Khi có lỗi mới, append thêm vào cuối file theo đúng format hiện tại.
- Mỗi mục luôn gồm 3 phần: `Chi tiết lỗi`, `Nguyên nhân gốc`, `Cách fix`.

Ghi chú mở rộng:

- Từ vòng rà soát này trở đi, file này không chỉ là log lỗi, mà còn là tài liệu kỹ thuật nhóm lỗi theo pattern chung.
- Mỗi lỗi nên được đọc cùng với tầng kiến trúc mà nó phá vỡ, không nên chỉ đọc như một lỗi rời rạc.
- Khi cần, mỗi mục có thể mở rộng thêm các phần: `Tầng ảnh hưởng`, `Tác động`, `Regression/Guard`.

## 1. Cách đọc file này

Nên đọc theo 3 lớp:

1. `Bản đồ lỗi theo tầng` để biết bug thuộc parser, planner, generator, executor hay UI/ops.
2. `Pattern kỹ thuật chung` để hiểu nguyên nhân hệ thống.
3. `Danh mục E..` để xem symptom và fix cụ thể.

Nếu chỉ đọc từng mã lỗi đơn lẻ, rất dễ quay lại cách vá từng case thay vì sửa đúng mô hình.

## 2. Bản đồ lỗi theo tầng kiến trúc

| Tầng | Mục tiêu | Nhóm lỗi chính |
| --- | --- | --- |
| Parsing / AST / Scope | Parse đúng SQL, giữ scope và alias lineage | E05, E09, E14, E22, E23, E24, E25, E26, E52, E57, E58 |
| Scenario Planning | Sinh scenario đúng, đủ, không thừa | E14, E15, E18, E19, E20, E50, E51, E52, E53 |
| Data Generation Core | Sinh dữ liệu thỏa query chứ không chỉ insert được | E06, E08, E10, E11, E16, E17, E18, E19, E20, E21, E27, E28, E31, E34, E35 |
| Schema / Type Safety | Tôn trọng max length, precision/scale, numeric range | E29, E30, E41, E42, E43, E44, E45, E46 |
| FK / PK / Insert Planning | Khép kín graph dữ liệu và insert đúng order | E01, E03, E04, E07, E12, E48, E49 |
| Sample / Realistic Data | Dữ liệu giống thật nhưng không nhiễm synthetic | E31, E32, E33, E34, E36, E37 |
| Join / Result Shape | Join ra đủ bảng và cardinality đúng | E20, E22, E27, E28, E47, E48, E49, E53, E54 |
| Script / CSV / UI / Ops | Script chạy được, log hữu ích, file/CSV đúng nghĩa | E38, E39, E40, E51, E55, E56 |

## 3. Pattern kỹ thuật chung cần nhớ

### P01. Insert được nhưng query không ra data

Đây là pattern sai lớn nhất trong các vòng sửa đầu tiên.

Triệu chứng:

- insert thành công;
- DB có row;
- nhưng câu `SELECT` gốc trả về 0 row.

Nguyên nhân hệ thống:

- tối ưu cho `insertability`, chưa tối ưu cho `query satisfiability`;
- literal, join predicate, aggregate predicate, subquery predicate chưa được ưu tiên đúng.

Các mã liên quan:

- E05, E06, E08, E16, E17, E21, E27, E58

### P02. Mất scope hoặc map nhầm alias/cột

Triệu chứng:

- predicate đúng text nhưng bị áp sai bảng/cột;
- `NOT EXISTS` bị rò lên outer scope;
- alias ở CTE/derived query không về được bảng gốc.

Nguyên nhân hệ thống:

- parser hoặc downstream mất lineage;
- fallback theo text quá rộng.

Các mã liên quan:

- E09, E23, E24, E25, E52, E57

### P03. Aggregate semantics khác scalar semantics

Triệu chứng:

- `COUNT >= 1` bị hiểu như sửa một giá trị cột;
- `AVG(...) >= X` fail dù giá trị nhìn có vẻ đúng;
- `Rows/Table = 1` nhưng query vẫn ra nhiều output group.

Nguyên nhân hệ thống:

- lẫn lộn giữa:
  - số row đóng góp aggregate;
  - giá trị measure của từng row;
  - số group đầu ra.

Các mã liên quan:

- E06, E17, E18, E19, E20, E35, E54, E58

### P04. Self-reference và self-join cần pattern synthesis riêng

Triệu chứng:

- bảng tự tham chiếu không tạo chain thật;
- query cặp sản phẩm mua cùng nhau không có data;
- join nội bộ cần repeated pattern nhưng engine chỉ sinh row độc lập.

Các mã liên quan:

- E10, E28

### P05. Literal trong SQL phải thắng mọi mode khác

Triệu chứng:

- câu SQL có `column = 'abc'` nhưng generator vẫn bám sample hoặc max-mode;
- literal trong `JOIN ON` không được sinh đúng;
- literal Unicode không map được.

Các mã liên quan:

- E21, E22, E25, E26

### P06. Sample mode và max mode phải tách cứng

Triệu chứng:

- tắt `Maxlength/MaxValue` nhưng vẫn thấy dữ liệu max-mode;
- sample mode học lại dữ liệu synthetic do tool vừa insert;
- max-length string dài nhưng vô nghĩa.

Các mã liên quan:

- E32, E36, E37, E41, E42

### P07. Type safety phải được chặn sớm

Triệu chứng:

- `Arithmetic overflow`;
- `smallint/tinyint/decimal` chỉ nổ ở bước insert;
- max-mode đẩy giá trị sang vùng query không thực thi được.

Các mã liên quan:

- E42, E43, E45, E46

### P08. Join semantics quan trọng hơn số lượng row thô

Triệu chứng:

- insert có data nhưng cột từ bảng join không ra;
- `LEFT JOIN miss` được sinh sai ngữ nghĩa;
- FK chuỗi/business key không join được dù schema đúng.

Các mã liên quan:

- E22, E27, E48, E49, E53

### P09. Bề mặt vận hành cũng là một phần của hệ thống

Triệu chứng:

- copy script chạy tay bị trùng key;
- log không hữu ích;
- CSV làm lẫn `NULL` và `empty string`;
- audit bị lỗi font.

Các mã liên quan:

- E38, E39, E40, E51, E55, E56

### P10. Không có regression thì fix chưa đáng tin

Triệu chứng:

- cùng một lớp lỗi lặp lại ở câu SQL khác;
- sửa xong một bug nhưng phá case cũ.

Nguyên nhân hệ thống:

- fix kiểu local patch;
- thiếu harness/regression để khóa hành vi.

Phạm vi:

- pattern này bao trùm gần như toàn bộ E01-E58

## 4. Quy tắc cập nhật file audit về sau

Khi thêm lỗi mới, cần ưu tiên mô tả theo hướng kỹ thuật common:

1. lỗi ở tầng nào;
2. symptom nhìn từ user là gì;
3. root cause mô hình là gì;
4. fix đang chốt ở parser, planner, generator, executor hay UI/ops;
5. regression/guard nào đang khóa nó lại.

Không nên chỉ note kiểu:

- `query X không ra data`

Mà nên note kiểu:

- `predicate aggregate bị solve sai vì engine lẫn aggregate semantics với scalar semantics`.

## 5. Bảng symptom -> tầng nghi vấn

| Symptom nhìn từ user | Tầng nghi vấn đầu tiên |
| --- | --- |
| Insert được nhưng `SELECT` không ra row | Parsing scope, scenario planning, data generation |
| `SELECT` ra ít row hơn kỳ vọng | Join semantics, aggregate cardinality, literal dominance |
| `SELECT` ra nhiều row hơn kỳ vọng | Result cardinality anchor, support rows, nhiều scenario dương cùng được chọn |
| `Arithmetic overflow` khi insert | Type safety, max-mode, numeric builder, normalizer |
| Lỗi FK khi insert | Generation scope closure, insert plan, ancestor synthesis |
| Lỗi unique/trùng key | Cleanup scope, key seed resolver, mutation unique |
| `LEFT JOIN` không ra cột bảng phải | Join semantics, `LEFT JOIN miss`, ON-literal solving |
| Điều kiện string/function không được thỏa | Function-aware expression solving, literal dominance, sample/max mode precedence |
| Tắt max-mode nhưng dữ liệu vẫn như max-mode | Sample baseline contamination, mode state reset |
| CSV import/export sai `NULL` / `""` | CSV semantics, parser/exporter alignment |
| UI scenario list khó hiểu hoặc thiếu nhánh | Scenario planner, label normalization, nested subquery coverage |

## 6. Nguyên tắc quyết định hướng fix

Nếu một bug có thể sửa ở nhiều tầng, ưu tiên theo thứ tự sau:

1. Sửa ở semantic model nếu root cause là mất nghĩa của SQL.
2. Sửa ở planner nếu bug là do hiểu sai branch/cardinality.
3. Sửa ở generator nếu semantic model đã đúng nhưng dữ liệu sinh ra chưa đúng.
4. Sửa ở executor nếu dữ liệu đúng nhưng insert plan/execution sai.
5. Chỉ sửa ở UI khi bug thật sự là lỗi hiển thị hoặc thao tác.

Không làm ngược lại. Ví dụ:

- nếu `EXISTS` không ra data vì `PaymentStatus` sinh sai, không vá ở UI;
- nếu `AVG(...)` fail vì aggregate đi vào expression-evaluator sai nhánh, không vá bằng special-case trong preview script;
- nếu `LEFT JOIN miss` sai nghĩa, không vá bằng cách ẩn scenario trên UI mà phải sửa analyzer.

## E01. Dọn dữ liệu theo sai phạm vi bảng
- Chi tiết lỗi: trước đây app chỉ dọn dữ liệu theo tập `generated tables`, trong khi một số bảng chỉ xuất hiện trong `insert plan`. Hệ quả là dữ liệu cũ còn sót lại trong DB và gây trùng khóa khi insert.
- Nguyên nhân gốc: phạm vi cleanup được tính từ output generate, không dựa trên toàn bộ kế hoạch insert thực tế.
- Cách fix: đổi cleanup sang dùng `planned table keys`, tức toàn bộ bảng thực sự tham gia insert đều được dọn trước khi ghi dữ liệu mới.

## E02. Mutation giá trị unique có thể không thay đổi
- Chi tiết lỗi: cơ chế sửa dữ liệu để né unique constraint có lúc tạo lại đúng giá trị cũ, dẫn đến retry vô ích hoặc vẫn trùng khóa.
- Nguyên nhân gốc: mutation không kiểm tra chặt chẽ điều kiện “giá trị mới phải khác giá trị hiện tại”.
- Cách fix: buộc mutation unique phải tạo ra giá trị mới thực sự khác giá trị cũ trước khi chấp nhận.

## E03. Dữ liệu hỗ trợ cho subquery bị rời rạc
- Chi tiết lỗi: dữ liệu sinh thêm cho subquery từng dùng offset kiểu `baseId + 500`, `baseId + 100`, làm đứt chuỗi FK/correlated chain.
- Nguyên nhân gốc: luồng sinh dữ liệu cho subquery tách rời hoàn toàn với allocator ID chính.
- Cách fix: dùng chung allocator ID với flow generate chính, sinh row theo dependency order và map correlated condition/FK chain thống nhất.

## E04. Insert plan không chuẩn hóa primary key
- Chi tiết lỗi: nếu insert plan chứa row có PK trùng hoặc PK null, executor cũ chỉ báo lỗi chứ không tự sửa.
- Nguyên nhân gốc: thiếu bước canonicalize PK trước khi insert.
- Cách fix: thêm bước đảm bảo PK hợp lệ, tự cấp lại PK khi cần và propagate sang FK con.

## E05. CTE không được đưa vào mô hình phân tích điều kiện
- Chi tiết lỗi: query insert được nhưng select không trả dữ liệu vì điều kiện nằm trong CTE không được engine nhìn thấy.
- Nguyên nhân gốc: parser chỉ phân tích query chính, bỏ qua `QuerySpecification` trong CTE.
- Cách fix: phân tích toàn bộ CTE body, đưa WHERE/HAVING/Subquery của CTE vào cùng execution model.

## E06. Positive path bỏ qua điều kiện HAVING
- Chi tiết lỗi: app sinh dữ liệu thỏa WHERE/JOIN nhưng không thỏa HAVING, nên query không ra row.
- Nguyên nhân gốc: generator không dùng aggregate constraints khi sinh positive data.
- Cách fix: đưa HAVING aggregate condition vào solver, để positive path cũng phải thỏa `SUM`, `AVG`, `COUNT`, v.v.

## E07. Không gom đủ ancestor closure cho FK graph
- Chi tiết lỗi: có trường hợp bảng con được sinh nhưng bảng cha không nằm trong scope, dẫn đến lỗi FK khi insert.
- Nguyên nhân gốc: generation scope chỉ lấy bảng trực tiếp từ query/subquery, không mở rộng theo toàn bộ tổ tiên FK.
- Cách fix: gom `main tables + subquery tables + FK ancestor closure` trước khi resolve insert order và generate data.

## E08. Điều kiện trong EXISTS không được áp vào dữ liệu sinh ra
- Chi tiết lỗi: row liên quan đến subquery có FK đúng nhưng cột business như `PaymentStatus` không đúng nên `EXISTS` fail.
- Nguyên nhân gốc: engine chỉ áp điều kiện ở query chính, không áp condition nằm trong `EXISTS`.
- Cách fix: đọc và giải condition trong subquery/EXISTS chung với positive solver của query.

## E09. Rò điều kiện từ NOT EXISTS sang outer scope
- Chi tiết lỗi: có lúc predicate của `NOT EXISTS` bị coi như predicate của outer WHERE, làm positive/negative bị đảo nghĩa.
- Nguyên nhân gốc: parser không tách scope predicate theo từng `QuerySpecification`.
- Cách fix: lọc predicate theo đúng scope query và giữ polarity riêng cho `EXISTS` và `NOT EXISTS`.

## E10. Self-reference không tạo được chain thật
- Chi tiết lỗi: các bảng tự tham chiếu như `Categories.ParentCategoryID` từng chỉ sinh `NULL` hoặc chain giả, khiến query self-join không trả dữ liệu.
- Nguyên nhân gốc: engine chưa có self-reference plan thật sự.
- Cách fix: thêm self-reference plan, ép số row tối thiểu đủ chiều sâu và sinh self-FK thành chain thật.

## E11. `Rows/Table` bị dùng sai nghĩa
- Chi tiết lỗi: người dùng chọn `1` nhưng app lại sinh nhiều row hơn chỉ vì query có aggregate.
- Nguyên nhân gốc: `Rows/Table` đồng thời đóng vai trò “số row mong muốn” và “số support rows cho aggregate”.
- Cách fix: tách support rows nội bộ khỏi semantics UI, chỉ tăng số row khi logic query thật sự bắt buộc.

## E12. Thiếu hậu kiểm FK sau khi insert
- Chi tiết lỗi: nếu executor từng phải dùng lối đi sửa lỗi/tạm thời, transaction chưa tự chứng minh toàn bộ FK hợp lệ trước khi commit.
- Nguyên nhân gốc: thiếu bước xác minh ràng buộc hậu insert.
- Cách fix: thêm post-insert FK validation trước khi `COMMIT`.

## E13. Recursive CTE chưa được khóa bằng regression
- Chi tiết lỗi: các truy vấn `recursive CTE` dễ có khoảng trống semantics dù query thường chạy được.
- Nguyên nhân gốc: thiếu harness riêng cho recursion path.
- Cách fix: thêm regression và giữ recursive CTE trong tập sanity chuẩn.

## E14. Analyzer làm phẳng cây boolean
- Chi tiết lỗi: scenario bị thừa hoặc thiếu khi query có `OR`, `NOT`, hoặc nhóm điều kiện lồng nhau.
- Nguyên nhân gốc: analyzer cũ đọc predicate theo danh sách phẳng, không dựng cây logic thật.
- Cách fix: dựng predicate tree và truth planner tối thiểu, từ đó sinh scenario theo boolean semantics chuẩn.

## E15. Tên scenario khó hiểu với người dùng
- Chi tiết lỗi: người dùng khó đọc hoặc khó hiểu scenario vì label quá kỹ thuật hoặc không mô tả rõ “tại sao fail”.
- Nguyên nhân gốc: UI hiển thị trực tiếp tên kỹ thuật từ analyzer.
- Cách fix: chuẩn hóa label scenario theo ngôn ngữ dễ hiểu hơn, hiển thị rõ expected result và predicate bị tác động.

## E16. Subquery support pass tạo dư witness rows
- Chi tiết lỗi: engine từng chèn thêm row phụ dù scenario đã đủ điều kiện, làm query trả nhiều row ngoài ý muốn.
- Nguyên nhân gốc: sau khi generate xong vẫn còn một support pass riêng cho subquery.
- Cách fix: bỏ witness pass mặc định, chỉ giữ một flow generate thống nhất theo truth map.

## E17. Solver aggregate ưu tiên sai thứ tự
- Chi tiết lỗi: cột đồng thời bị ràng buộc bởi HAVING và subquery/NOT EXISTS có thể bị chốt giá trị theo ràng buộc yếu hơn, làm query rỗng.
- Nguyên nhân gốc: thứ tự giải constraint chưa thống nhất.
- Cách fix: đưa aggregate constraint vào cùng solver với predicate thường và giải theo ưu tiên semantics của query.

## E18. `COUNT` negative bị xử lý như value predicate
- Chi tiết lỗi: scenario âm cho `COUNT(...) >= 1` có lúc vẫn sinh đủ row, nên COUNT vẫn đúng và scenario âm thành dương.
- Nguyên nhân gốc: engine xem COUNT như so sánh giá trị cột thay vì cardinality của tập row.
- Cách fix: chuyển COUNT negative sang row-count semantics thật, điều khiển số row đóng góp aggregate thay vì sửa giá trị cột.

## E19. `COUNT` boundary ép sai vào giá trị khóa
- Chi tiết lỗi: boundary của `COUNT >= 1` từng được biểu diễn bằng cách sửa `ProductID = 1`, hoàn toàn sai bản chất.
- Nguyên nhân gốc: boundary planner dùng chung logic với scalar predicate.
- Cách fix: boundary của COUNT được hiểu là đúng số row đóng góp tối thiểu, không đụng vào key value vô nghĩa.

## E20. `Rows/Table = 1` nhưng query vẫn ra nhiều output group
- Chi tiết lỗi: dù người dùng chỉ muốn 1 kết quả, query có aggregate/grouping vẫn trả nhiều row.
- Nguyên nhân gốc: planner chỉ khống chế số row vật lý từng bảng, không khống chế `output group cardinality`.
- Cách fix: thêm khái niệm `result cardinality anchor tables`, giữ support rows nội bộ nhưng ép chúng nằm trong cùng output group khi có thể.

## E21. Điều kiện literal trong SQL không được ưu tiên tuyệt đối
- Chi tiết lỗi: query có `column = 'abc'` hoặc `column = 123` nhưng generator vẫn sinh giá trị gần đúng, lấy từ sample, hoặc max-mode thay vì đúng literal.
- Nguyên nhân gốc: constraint từ literal chưa có độ ưu tiên cao nhất.
- Cách fix: mọi literal xuất hiện trong `WHERE`, `JOIN`, `HAVING`, expression predicate đều phải được map chính xác và ưu tiên hơn sample/max mode.

## E22. Predicate literal trong `JOIN ON` chưa được phân tích đủ
- Chi tiết lỗi: câu kiểu `LEFT JOIN ... ON HLC.NmIdentifyTyp = '9' AND ...` từng không tạo data đúng để join.
- Nguyên nhân gốc: parser tập trung vào equality giữa cột với cột, chưa cover đủ literal ngay trong `ON`.
- Cách fix: coi `JOIN ON` là nguồn predicate hạng nhất, parse literal và dùng nó để sinh row joinable.

## E23. Derived subquery lồng nhiều tầng không sinh đủ scenario
- Chi tiết lỗi: scenario trong subquery lồng sâu hoặc derived table bị thiếu dù cú pháp SQL không đặc biệt.
- Nguyên nhân gốc: analyzer chưa đi xuyên đầy đủ qua nhiều lớp alias/query con.
- Cách fix: thêm lineage cho derived query và phân tích predicate xuyên nhiều tầng.

## E24. Mất alias lineage qua derived query
- Chi tiết lỗi: condition đúng về cú pháp nhưng không map được về bảng/cột gốc, khiến generate sai hoặc không generate.
- Nguyên nhân gốc: alias rewrite chưa giữ được nguồn gốc cột sau khi đi qua subquery/CTE/derived table.
- Cách fix: bổ sung alias lineage và column origin mapping trong parser.

## E25. Predicate kiểu expression string không được giải
- Chi tiết lỗi: các biểu thức như `ISNULL(col1,'') + ISNULL(col2,'') = 'X'` không được map thành dữ liệu đúng.
- Nguyên nhân gốc: engine chỉ giải được so sánh cột đơn, chưa evaluate expression target.
- Cách fix: thêm evaluator cho expression predicate, hỗ trợ nối chuỗi, ISNULL và các pattern phổ biến.

## E26. Literal Unicode không map được
- Chi tiết lỗi: literal kiểu `N'間'` hoặc các chuỗi Unicode khác không được sinh đúng.
- Nguyên nhân gốc: solver literal chưa xử lý đầy đủ Unicode string và expression target.
- Cách fix: đưa Unicode literal vào cùng pipeline match/evaluate và giữ nguyên khi sinh dữ liệu.

## E27. Không đảm bảo join ra dữ liệu cho mọi bảng
- Chi tiết lỗi: có lúc query insert thành công nhưng một số cột từ bảng join không ra vì row không thực sự join được.
- Nguyên nhân gốc: engine thiên về “insert hợp lệ” hơn “join trả dữ liệu”.
- Cách fix: chuyển tiêu chí thành `query-satisfiable dataset`, bắt buộc tạo row joinable cho tất cả bảng liên quan trong result.

## E28. Self-join pair pattern không được tổng hợp
- Chi tiết lỗi: truy vấn kiểu mua kèm sản phẩm theo cặp trong cùng order không có data dù schema đúng.
- Nguyên nhân gốc: planner không hiểu pattern “cùng OrderID, hai ProductID khác nhau, lặp lại trên nhiều order”.
- Cách fix: thêm pair-pattern synthesis cho self-join trên bảng fact như `OrderItems`.

## E29. Dùng `Start ID` toàn cục là sai kiến trúc
- Chi tiết lỗi: một seed chung cho mọi bảng không phù hợp với schema thực tế và dễ làm key out-of-range.
- Nguyên nhân gốc: generator không dựa vào trạng thái thật của từng bảng trong DB.
- Cách fix: bỏ dần cách nghĩ “Start ID toàn cục”, chuyển sang resolve next key theo từng bảng từ DB.

## E30. Không đọc next key theo từng bảng
- Chi tiết lỗi: key mới sinh ra có thể đè vào vùng ID hiện có của bảng hoặc vượt phạm vi kiểu dữ liệu.
- Nguyên nhân gốc: thiếu table-level key seed resolver.
- Cách fix: lấy next key theo từng bảng, xét cả `MAX(PK)` và trạng thái identity khi cần.

## E31. Dữ liệu mặc định quá giả tạo
- Chi tiết lỗi: generator mặc định sinh `TestData_1`, `TestData_2`... khiến dữ liệu thiếu thực tế và khó test nghiệp vụ thật.
- Nguyên nhân gốc: engine thiên về placeholder thay vì sample/profile-driven synthesis.
- Cách fix: chuyển hướng mặc định sang sample-driven mutation khi có dữ liệu thật trong DB.

## E32. Sample mode bị nhiễm dữ liệu synthetic
- Chi tiết lỗi: sau khi tool insert dữ liệu của chính nó, lần generate sau lại học ngược từ dataset synthetic đó.
- Nguyên nhân gốc: sample được lấy trực tiếp từ DB hiện thời, không phân biệt dữ liệu gốc và dữ liệu do tool sinh.
- Cách fix: thêm baseline sample cache và cơ chế nhận diện sample bẩn/synthetic.

## E33. Chưa có profile học phân bố dữ liệu thật
- Chi tiết lỗi: chỉ lấy 1 sample row là chưa đủ để hiểu pattern phân bố thật của cột.
- Nguyên nhân gốc: chưa có `ColumnProfileCollector`/`distribution learning` đúng nghĩa.
- Cách fix: thiết kế hướng profile-driven generator và dùng sample row như template, không nhầm với distribution.

## E34. Giá trị giữa các cột trong cùng một row quá giống nhau
- Chi tiết lỗi: nhiều cột khác nhau cùng ra gần như một chuỗi, làm khó phát hiện lỗi map nhầm cột.
- Nguyên nhân gốc: seed cho nhiều cột bắt đầu giống nhau và thiếu fingerprint theo cột.
- Cách fix: thêm `column fingerprint` và `row fingerprint`, đảm bảo mỗi cột/record có dấu hiệu nhận diện riêng.

## E35. Các measure aggregate quá giống nhau
- Chi tiết lỗi: các cột như `RevenueAmount`, `AvgUnitPrice`, `RevenuePerOrder`, `RevenuePerLine` dễ ra cùng một số.
- Nguyên nhân gốc: graph fact quá phẳng, thiếu đa dạng trong quantity/unit price/line total.
- Cách fix: thêm aggregate diversity support cho các bảng fact và các cột measure.

## E36. Checkbox `Maxlength/MaxValue` bị rò sang normal mode
- Chi tiết lỗi: bỏ chọn checkbox nhưng dữ liệu vẫn mang dấu vết max-mode.
- Nguyên nhân gốc: sample-mode học lại dữ liệu do max-mode vừa insert, và state cũ không được reset sạch.
- Cách fix: tách cứng 2 mode, clear dataset/script cũ khi đổi mode, và sample-mode đọc từ baseline sample.

## E37. Pattern chuỗi max-length không có ý nghĩa
- Chi tiết lỗi: một số chuỗi max-length bị sinh kiểu `LSLSLS...`, rất khó đọc và không giống dữ liệu thật.
- Nguyên nhân gốc: fallback string generator ưu tiên lấp đầy độ dài hơn là giữ semantic pattern.
- Cách fix: đổi sang semantic text có nghĩa, ưu tiên pattern gần với nghiệp vụ và hỗ trợ tiếng Nhật/Unicode.

## E38. Không phân biệt `NULL` và chuỗi rỗng khi export/import CSV
- Chi tiết lỗi: `NULL` và `""` có thể bị trộn nghĩa khi đi qua CSV.
- Nguyên nhân gốc: exporter/importer chưa mã hóa tường minh 2 trạng thái này.
- Cách fix: chuẩn hóa `NULL -> ,,` và `empty string -> ,""`, giữ đúng ở cả 2 chiều export/import.

## E39. Script preview/copy thiếu cleanup tương ứng với `Insert to DB`
- Chi tiết lỗi: người dùng copy script rồi chạy tay rất dễ bị trùng key vì dữ liệu cũ chưa bị xóa.
- Nguyên nhân gốc: script preview chỉ có phần insert, không có phần cleanup an toàn.
- Cách fix: script thực thi phải gồm `cleanup + insert` trong cùng transaction.

## E40. `Full Log` không phải log vận hành thật
- Chi tiết lỗi: ô log từng trộn lẫn preview SQL hoặc thông tin không hữu ích với user.
- Nguyên nhân gốc: log UI chưa tách rõ event log và script preview.
- Cách fix: chỉ log các sự kiện runtime có ích như Analyze/Generate/Insert/Export/Import/Error/Warning.

## E41. Max mode bị lớp adjustment ghi đè
- Chi tiết lỗi: bật `Maxlength/MaxValue` nhưng một số cột số vẫn ra giá trị nhỏ do bị semantic adjustment ghi đè về sau.
- Nguyên nhân gốc: pipeline max-mode không được bảo toàn đến lớp cuối cùng.
- Cách fix: các bước adjustment cuối phải tôn trọng mode và chỉ dùng `max-safe descending values`.

## E42. Max mode đẩy numeric lên type-max mù quáng
- Chi tiết lỗi: query có CAST/aggregate/arithmetic bị overflow dù insert thành công.
- Nguyên nhân gốc: max-mode từng đẩy số lên gần giới hạn kiểu dữ liệu, không xét ngữ cảnh thực thi query.
- Cách fix: dùng `practical execution-safe upper boundary` thay cho type max thô.

## E43. Low-level safe value builder thiếu context của query
- Chi tiết lỗi: ở vài nhánh, engine xuống đến tầng build số lớn mà làm mất thông tin cột đó đang nằm trong `SUM`, `AVG`, `*`, `/`, `CAST`.
- Nguyên nhân gốc: query context không được truyền xuyên suốt đến tầng cuối.
- Cách fix: truyền `ParsedQuery`/execution context xuống tận builder low-level.

## E44. Mutation chuỗi ngắn có thể ném `Index and length must refer...`
- Chi tiết lỗi: vài sample string ngắn gây exception khi cắt/chèn token.
- Nguyên nhân gốc: path mutate chuỗi giả định độ dài tối thiểu không đúng.
- Cách fix: harden logic substring/replace và kiểm tra biên trước khi thao tác.

## E45. Không chặn sớm overflow với `tinyint`/`smallint`
- Chi tiết lỗi: key hoặc giá trị sinh ra vượt phạm vi kiểu dữ liệu, chỉ nổ ở bước insert.
- Nguyên nhân gốc: allocator và generator chưa kiểm tra type range từ sớm.
- Cách fix: chặn sớm ngay lúc generate, fail fast nếu block ID hoặc giá trị vượt range.

## E46. `smallint` vẫn có thể overflow trong thực tế
- Chi tiết lỗi: dù đã có guard ở một số nhánh, vẫn có trường hợp giá trị kiểu `smallint` bị sinh thành `90092` và nổ khi insert.
- Nguyên nhân gốc: không phải mọi đường đi sinh số đều đi qua cùng một range guard.
- Cách fix: rà soát toàn bộ numeric synthesis path, thống nhất clamp/range check cho `tinyint`, `smallint`, `int`, `bigint`, decimal, money, v.v.

## E47. Phân loại bảng/cột nghiệp vụ chưa chuẩn
- Chi tiết lỗi: bảng như `Inventory`, `Reviews`, hay cột như `CostPrice`, `QuantityOnHand` từng bị nhận diện nhầm sang nhóm `line/item`, dẫn đến giá trị max-mode không an toàn.
- Nguyên nhân gốc: heuristic semantic quá hẹp và ưu tiên sai thứ tự.
- Cách fix: mở rộng semantic classification, ưu tiên nhận diện đúng nhóm inventory/review/measure/rate/count trước các heuristic tổng quát hơn.

## E48. FK chuỗi/phi số không được map bằng giá trị thật
- Chi tiết lỗi: một số join/FK không dựa trên numeric ID mà dựa trên mã chuỗi, nhưng engine từng cố dùng row id nên join không ra.
- Nguyên nhân gốc: chiến lược FK resolution nghiêng hoàn toàn về numeric referenceable ids.
- Cách fix: tách 2 đường: FK số dùng ID; FK chuỗi/phi số copy giá trị thực từ row được tham chiếu.

## E49. Thứ tự resolve giữa ID và business key chưa đúng
- Chi tiết lỗi: có trường hợp cột cần copy giá trị business key nhưng engine lại đi theo path ID hoặc ngược lại.
- Nguyên nhân gốc: chưa có rule thống nhất theo kiểu dữ liệu và semantics join.
- Cách fix: đưa rule phân nhánh vào resolver: numeric FK ưu tiên ID; non-numeric join key ưu tiên actual related value.

## E50. Boundary bị tách thành scenario riêng gây nhiễu
- Chi tiết lỗi: có lúc user chỉ cần positive data nhưng UI lại sinh thêm nhiều `Boundary` scenario dễ gây hiểu nhầm và tăng số row ngoài ý muốn.
- Nguyên nhân gốc: boundary coverage được model như scenario độc lập thay vì một phần của positive path.
- Cách fix: normal mode mặc định sinh data biên cho range predicate ngay trong positive path, còn UI chỉ giữ scenario cần cho branch coverage thật sự.

## E51. Thiếu cơ chế chọn/bỏ chọn tất cả scenario thuận tiện
- Chi tiết lỗi: khi số scenario nhiều, người dùng phải tick tay từng mục, dễ sót và khó thao tác.
- Nguyên nhân gốc: UI không có cơ chế bulk-select trực tiếp trong vùng scenario.
- Cách fix: thêm checkbox `All` trong card `Scenarios`, đồng bộ 2 chiều với danh sách scenario.

## E52. Scenario trong subquery lồng sâu vẫn có lỗ hổng
- Chi tiết lỗi: query không quá lạ nhưng condition nằm sâu trong subquery vẫn có thể không sinh đủ scenario.
- Nguyên nhân gốc: analyzer chưa bao phủ đầy đủ multi-level nested subquery, nhất là khi lồng derived query + alias rewrite + aggregate.
- Cách fix: tiếp tục mở rộng parser/analyzer để mọi predicate có thể truy vết được về bảng/cột gốc và tham gia branch planning.

## E53. `LEFT JOIN miss` từng bị sinh sai ngữ nghĩa
- Chi tiết lỗi: có trường hợp analyzer vẫn tạo scenario `LEFT JOIN miss` dù right side bị predicate ở WHERE ép thành inner-join semantics.
- Nguyên nhân gốc: join analyzer chỉ nhìn loại join, chưa xét toàn bộ predicate sau join.
- Cách fix: chỉ tạo `LEFT JOIN miss` khi right alias thực sự có thể vắng mặt mà query vẫn hợp lệ theo semantics tổng thể.

## E54. Tương tác giữa derived query, aggregate và window function chưa đủ planner support
- Chi tiết lỗi: một số query có derived table + aggregate + `DENSE_RANK`/window function không ra data dù query hợp lệ.
- Nguyên nhân gốc: planner minimum rows và result-shape support chưa xét đầy đủ các pattern phân tầng này.
- Cách fix: mở rộng planner để hiểu các bảng/CTE trung gian, nhu cầu witness rows và cardinality anchor cho query có window function.

## E55. File audit cũ bị lỗi font và không có dấu
- Chi tiết lỗi: tài liệu tổng hợp lỗi trước đó bị mojibake, thiếu dấu tiếng Việt và khó đọc.
- Nguyên nhân gốc: file không được duy trì với encoding ổn định, nội dung từng bị ghi bằng chuỗi sai mã hóa.
- Cách fix: viết lại toàn bộ bằng tiếng Việt chuẩn và lưu lại theo encoding nhất quán.

## E56. Encoding của file audit không được chốt rõ
- Chi tiết lỗi: cùng một file có thể mở đúng ở nơi này nhưng lỗi font ở nơi khác, nhất là trên Windows.
- Nguyên nhân gốc: file không được lưu theo chuẩn phù hợp với editor/terminal đang dùng.
- Cách fix: chốt chuẩn `UTF-8 BOM` cho file audit và duy trì thống nhất về sau.

## E57. Điều kiện bị map nhầm sang cột khác chỉ vì trùng tên cột
- Chi tiết lỗi: predicate như `c.IsActive = 1` hoặc `co.IsActive = 1` từng bị áp nhầm sang các bảng khác cũng có cột `IsActive`, làm positive path bị méo dù parser đã đọc đúng SQL.
- Nguyên nhân gốc: bước `ConditionTargetsColumn` từng fallback theo `ExpressionText.Contains(columnName)`, nên chỉ cần expression có chữ `IsActive` là nhiều cột cùng tên đều bị coi là target.
- Cách fix: siết lại logic targeting theo alias/cột thực, chỉ dùng fallback expression khi không có reference rõ ràng và không phải subquery predicate.

## E58. Predicate aggregate bị expression-evaluator xử lý sai ở bước generate
- Chi tiết lỗi: các điều kiện như `AVG(p.StandardCost) >= 10.00` vẫn bị fail khi generate, dù cột đã được sinh đúng `10.00`.
- Nguyên nhân gốc: aggregate predicate bị đưa vào expression-evaluator như một biểu thức tổng hợp thật, trong khi ở bước generate theo từng cột chỉ cần so trực tiếp giá trị cột với ngưỡng aggregate target.
- Cách fix: aggregate-target ở bước solve cột phải đi theo nhánh so sánh trực tiếp, không ép qua expression-evaluator.

## E59. Max-mode của string không fill đúng `MaxLength`
- Chi tiết lỗi: khi bật `Maxlength/MaxValue`, một số cột chuỗi như `varchar(100)` vẫn chỉ sinh giá trị ngắn kiểu `TestData_xxx` hoặc token ngắn, thay vì đúng 100 ký tự.
- Nguyên nhân gốc: max-mode string builder trước đây chỉ tạo ra một chuỗi “hợp lệ và có nghĩa”, nhưng không buộc phải fill đủ độ dài mục tiêu. Các nhánh `email/url/code` đặc biệt dễ dừng ở chuỗi ngắn hơn `MaxLength`.
- Cách fix: bổ sung exact-fill strategy cho max-mode string. Khi checkbox bật, generator phải tạo chuỗi đúng bằng `ResolveTargetStringLength(column)`, kể cả với `email`, `url`, `code`, và text semantic thường. Đồng thời thêm regression riêng cho query join đơn giản với `Products / Categories / Brands` để khóa hành vi này.

## E60. Người dùng dễ nhầm giữa `analysis tables` và `generation scope`
- Chi tiết lỗi: có query chỉ phân tích ra 3 bảng trong SQL, nhưng script generate lại xuất hiện thêm nhiều bảng khác như `Suppliers`, `Addresses`, `Cities`, `Countries`, làm người dùng tưởng engine đang xử lý thừa.
- Nguyên nhân gốc: hệ thống có hai tập bảng khác nhau nhưng chưa được diễn giải rõ:
  - `analysis tables`: chỉ là các bảng xuất hiện trực tiếp trong SQL.
  - `generation scope`: là `analysis tables` cộng toàn bộ FK ancestor closure cần để insert được dataset khép kín.
- Cách fix: giữ nguyên semantics phân tích truy vấn, nhưng tài liệu hóa rõ sự khác biệt này và dùng regression để chứng minh ancestor closure là chủ đích kiến trúc chứ không phải side effect ngẫu nhiên.

## E61. Script preview từng có FK nội bộ không khép kín dù ancestor closure đã được sinh
- Chi tiết lỗi: có trường hợp script preview đã sinh đủ các bảng cha như `Countries -> Cities -> Addresses -> Suppliers -> Brands -> Products`, nhưng giá trị FK trong chính script lại không match nhau, ví dụ row con vẫn giữ giá trị sample/seed cũ thay vì trỏ vào row cha vừa sinh.
- Nguyên nhân gốc: dù engine đã mở rộng `generation scope` đúng, một số giá trị FK vẫn có thể bị trôi sau bước resolve ban đầu, làm dataset preview không còn đóng kín local FK graph.
- Cách fix: thêm hậu kiểm `local FK closure` ngay trong `DataGenerationEngine`:
  - sau khi sinh xong mỗi scenario, engine rà toàn bộ FK giữa các bảng đã có mặt trong scenario;
  - nếu FK chưa match row cha cục bộ, nó sẽ tự align lại theo parent row tương ứng;
  - nếu vẫn không khép kín được, generation fail sớm thay vì phát ra script lỗi.
  Đồng thời thêm regression cho query join đơn giản `Products + Categories + Brands` nhưng schema có ancestor closure sâu và sample rows cố tình chứa FK sai để khóa lớp lỗi này.

## E62. Function-aware string solver từng phá semantics `MaxLength` trong max-mode
- Chi tiết lỗi: với các điều kiện kiểu `CHARINDEX('a', LOWER(p.ProductName)) > 0`, engine có thể sinh được chuỗi thỏa hàm chuỗi, nhưng giá trị trả về lại ngắn như `ademoZ` thay vì fill đúng `varchar(100)` khi bật `Maxlength/MaxValue`.
- Nguyên nhân gốc: nhánh `function-aware string solver` dùng `FitSemanticString(...)`, mà hàm này chỉ cắt chuỗi khi quá dài chứ không pad lên đúng độ dài mục tiêu. Kết quả là string thỏa predicate nhưng phá invariant `LEN(value) == MaxLength` của max-mode.
- Cách fix: khi `UseMaxLengthMaxValueMode = true`, mọi candidate sinh từ function-aware solver phải đi qua exact-fill strategy (`RepeatPhraseToExactLength(...)`) thay vì `FitSemanticString(...)`. Đồng thời thêm regression riêng cho query có `LEN + CHARINDEX + LOWER + LEFT + RIGHT + SUBSTRING + REPLACE + TRIM` để khóa cả hai yêu cầu:
  - chuỗi thỏa function predicates;
  - chuỗi vẫn đúng `MaxLength`.

## E63. Parser chưa dựng AST đầy đủ cho `NULLIF`, `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT`
- Chi tiết lỗi: các predicate như `NULLIF(TRIM(p.ProductName), '') IS NOT NULL` hoặc `TRY_CONVERT(DECIMAL(18,2), oi.LineTotal) > 0` nhìn hợp lệ nhưng engine không target đúng cột hoặc không evaluate đúng expression.
- Nguyên nhân gốc: `PredicateTreeBuilder` trước đây chỉ xem `FunctionCall`, `BinaryExpression`, `UnaryExpression` là expression phức tạp. Các node ScriptDom chuyên biệt như `NullIfExpression`, `CastCall`, `ConvertCall`, `TryCastCall`, `TryConvertCall` chưa được chuyển thành `ScalarExpressionInfo`, cũng chưa được quét cột tham chiếu.
- Cách fix:
  - mở rộng `BuildScalarExpression(...)` để convert các node trên thành `FunctionScalarExpressionInfo`;
  - mở rộng `ExtractColumnReference(...)` và `CollectReferencedColumns(...)` cho cùng tập node;
  - với `CAST/CONVERT/TRY_*`, lưu thêm type target vào argument list để evaluator có đủ ngữ cảnh.

## E64. Expression evaluator từng không hiểu đúng `NOT LIKE`, `NULLIF`, `IN/NOT IN` dạng hàm, và `TRY_CONVERT`
- Chi tiết lỗi: query có `LOWER(p.ProductName) NOT LIKE '%test%'`, `NULLIF(TRIM(...), '') IS NOT NULL`, `LOWER(TRIM(o.OrderStatus)) NOT IN (...)`, `TRY_CONVERT(DECIMAL(18,2), oi.LineTotal) > 0` vẫn có thể không ra data dù parser đã đọc được SQL.
- Nguyên nhân gốc:
  - `LIKE` được evaluate nhưng không đảo nghĩa khi `IsNegated = true`;
  - evaluator chưa có semantics cho `NULLIF`, `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT`;
  - `TryEvaluateExpressionConditionTarget(...)` chưa cover `IN`, `NOT IN`, `BETWEEN` cho các left-expression phức tạp.
- Cách fix:
  - đảo nghĩa đúng cho `NOT LIKE` và `NOT BETWEEN`;
  - thêm `EvaluateNullIf(...)` và `EvaluateConversionFunction(...)`;
  - thêm `ConvertUsingSqlType(...)` với mapping cho `int`, `bigint`, `smallint`, `tinyint`, `decimal/numeric`, `float/real`, `date/datetime/datetime2`, `datetimeoffset`, `time`, và string types;
  - mở rộng expression-target evaluator cho `IN`, `NOT IN`, `BETWEEN`.

## E65. String solver chưa đọc được “hint động” từ vế hàm còn lại của join
- Chi tiết lỗi: predicate kiểu `CHARINDEX(LEFT(LOWER(TRIM(b.BrandName)), 1), LOWER(TRIM(p.ProductName))) > 0` vẫn có thể fail dù engine hiểu `CHARINDEX`, vì `ProductName` không được chèn ký tự đầu của `BrandName`.
- Nguyên nhân gốc: solver cũ chỉ thu hint chuỗi từ literal tĩnh (`'a'`, `'test'`, `'%demo%'`) và `DynamicStringValues`, chứ chưa evaluate được các subexpression đã có dữ liệu thật ở bảng khác như `LEFT(LOWER(TRIM(b.BrandName)), 1)`.
- Cách fix:
  - thêm `ExtractRuntimeStringHints(...)`;
  - duyệt cây expression để lấy các subexpression không phụ thuộc target column (`EnumerateHintExpressions(...)`);
  - evaluate trực tiếp các subexpression này trên scenario hiện tại để lấy hint thật, rồi đưa vào candidate builder.

## E66. Generator từng sinh candidate thô trái nghĩa với predicate âm
- Chi tiết lỗi: dù parser/evaluator có thể hiểu `NOT LIKE` hoặc `NOT BETWEEN`, bước generate candidate sơ cấp vẫn có lúc sinh theo nghĩa dương, làm solver phải “may mắn” loại bỏ về sau hoặc fail không ra row.
- Nguyên nhân gốc: `GenerateConditionValue(...)` xử lý `LIKE` và `BETWEEN` theo operator gốc mà chưa xét `IsNegated`.
- Cách fix:
  - với `LIKE`, tách rõ `shouldMatchLike = condition.IsNegated ? !satisfy : satisfy`;
  - với `BETWEEN`, đảo `inside/outside` khi `IsNegated = true`;
  - giữ cho generator và evaluator dùng cùng semantics, tránh lệch giữa bước sinh candidate và bước kiểm chứng.

## E67. Export binary từng đi theo đường `GetValue()` nguyên khối
- Chi tiết lỗi: với cột `varbinary/image` lớn như ảnh hoặc video, export CSV có thể chậm và tốn RAM vì kéo toàn bộ blob vào memory rồi mới hex hóa.
- Nguyên nhân gốc: exporter dùng `reader.GetValue(i)` cho mọi kiểu dữ liệu, kể cả binary lớn, trong khi query đã mở `SequentialAccess`.
- Cách fix:
  - giữ `SequentialAccess`;
  - với cột binary, dùng `reader.GetBytes(...)` theo chunk để stream ra CSV;
  - ghi trực tiếp từng chunk hex vào `StreamWriter` thay vì materialize toàn bộ blob thành một `byte[]` rồi mới convert.
  - bổ sung kiểm tra `totalBytes == exportedBytes` để fail sớm nếu stream nhị phân bị đọc thiếu.

## E68. Import CSV từng đọc cả file media-hex vào RAM trước khi parse
- Chi tiết lỗi: khi CSV chứa payload nhị phân lớn đã được hex hóa, `File.ReadAllTextAsync(...)` khiến load chậm, đỉnh memory cao, và parser không phù hợp cho file lớn.
- Nguyên nhân gốc: importer parse theo mô hình “đọc hết file -> parse toàn bộ records”, không theo stream.
- Cách fix:
  - thay bằng parser CSV streaming trên `FileStream + StreamReader + buffered char reader`;
  - parse record tuần tự, không giữ toàn bộ file trong memory;
  - với binary hex, thêm kiểm tra chặt `odd length` trước khi `Convert.FromHexString(...)`;
  - thêm regression `binary csv integrity` để khóa invariant: payload dài vẫn round-trip đủ byte, không thiếu byte như lỗi cũ.
