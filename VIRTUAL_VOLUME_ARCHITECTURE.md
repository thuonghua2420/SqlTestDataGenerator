# Kiến Trúc Ẩn Dữ Liệu: Virtual Volume / File Container

Tài liệu này mô tả chi tiết phương pháp làm thế nào để "tàng hình" dữ liệu trên Windows, biến một lượng lớn dữ liệu (ví dụ 5GB) thành một khối duy nhất không thể đọc được bằng Explorer thông thường, mà chỉ có thể truy xuất thông qua Ứng dụng quản lý của bạn.

## 1. Tổng quan hai hướng tiếp cận của Phương pháp 1

Phương pháp Virtual Volume (Volume ảo) có 2 cách để triển khai trong thực tế, phụ thuộc vào mong muốn trải nghiệm của bạn:

### Cách 1A: App-Level Virtual File System (Khuyên dùng cho sự đơn giản & độc lập)
*   **Mô tả:** Ứng dụng của bạn hoạt động giống như WinRAR hoặc một trình quản lý file riêng biệt. Dữ liệu được nhét chung vào 1 file lớn (vd: `my_secret_data.bin`). Tệp này được mã hóa toàn bộ.
*   **Cách sử dụng:** Người dùng muốn xem/thêm file thì MỞ phần mềm của bạn lên, nhập mật khẩu. Phần mềm sẽ render ra 1 giao diện giống Windows Explorer. Khi click đúp vào xem ảnh/video bên trong, phần mềm sẽ giải mã file đó lên bộ nhớ RAM và hiển thị (*hoặc ghi tạm ra `%TEMP%` rồi xóa sau khi xem*).
*   **Ưu điểm:** Cực kỳ dễ triển khai, máy người dùng không cần cài thêm driver hệ thống. Cầm file `.bin` mang sang máy khác vẫn mở được nếu có App của bạn.
*   **Nhược điểm:** Bạn phải tự code giao diện người dùng (UI) giống Explorer để duyệt nội dung.  

### Cách 1B: System-Level Mount (Giống VeraCrypt)
*   **Mô tả:** Ứng dụng của bạn sử dụng một kernel-driver ảo hóa (như **Dokany** hoặc **WinFSP**). Nó sẽ biến file `my_secret_data.bin` thành hẳn một ổ đĩa `Z:\` trên My Computer.
*   **Cách sử dụng:** Mở phần mềm, bấm "Mount". Lập tức trên Windows xuất hiện ổ `Z:\`. Người dùng thao tác với ổ `Z:\` này như một cái USB bình thường (copy, paste, edit trực tiếp bằng Chrome/Word...). Bấm "Unmount", ổ `Z:\` biến mất.
*   **Ưu điểm:** Tiện dụng tối đa cho người dùng. Không cần viết lại giao diện quản lý file.
*   **Nhược điểm:** Máy tính bắt buộc phải cài đặt Dokany Driver (đi kèm bộ cài phần mềm của bạn). Phức tạp hơn trong việc lập trình.

---

## 2. Luồng Hoạt Động Cốt Lõi (Core Workflow)

Dưới đây là luồng hoạt động chung (dùng cho Cách 1A - do tính độc lập cao):

### Bước 1: Khởi tạo/Tạo mới (Creation)
1. Người dùng chọn tạo "Kho lưu trữ bí mật" trên phần mềm.
2. Ứng dụng tạo một file có tên vô thưởng vô phạt trên ổ cứng, ví dụ `system_cache.dat`.
3. Ứng dụng thiết lập một dải byte đầu tiên của file này (Header) chứa thông tin: Phiên bản, Salt mã hóa, Bảng tra cứu thu nhỏ (File Allocation Table ảo). Toàn bộ Header này được mã hóa bằng AES-256 dựa trên Mật khẩu của người dùng.

### Bước 2: Thêm file vào kho ảnh/dữ liệu (Import)
1. Khi người dùng kéo thả thư mục 5GB vào Ứng dụng.
2. Ứng dụng đọc từng byte của các file ngoài đời thực.
3. Chạy qua thuật toán mã hóa (VD: `AesStream` trong .NET).
4. Nối đuôi (Append) các dòng byte đã mã hóa này liên tục vào file `system_cache.dat`.
5. Cập nhật lại Bảng FAT ảo ở Header (ghi chép lại: File `anh1.jpg` bắt đầu từ byte số 1000 đến byte số 5000 trong file tổng...).
6. Ghi đè lại Bảng FAT lên file. Xóa file gốc bên ngoài bằng chuẩn xóa an toàn (Ghi đè byte 0 sau đó xóa cấu trúc).

### Bước 3: Đọc/Mở file trong kho (Read/Export)
1. Ứng dụng giải mã Header bằng Password người dùng nhập lúc mở khóa.
2. Ứng dụng biết được vị trí của file cần mở (VD: user click mở file `anh1.jpg`).
3. Dò theo Bảng FAT ảo, ứng dụng trích xuất luồng byte từ vị trí 1000 -> 5000.
4. Giải mã luồng byte đó.
5. Đẩy thẳng luồng byte đã giải mã vào trình hiển thị ảnh (nếu app hỗ trợ preview) HOẶC xuất tạm ra RAM Drive/thư mục Temp để người dùng xem.

### Bước 4: Đóng ứng dụng (Lock/Cleanup)
1. Xóa mọi file tạm trên ổ cứng (nếu đã tạo để preview).
2. Xóa các biến lưu trữ Mật Khẩu, Bảng Trạng Thái trên RAM.
3. Đóng luồng (FileStream) khóa lại tập tin `system_cache.dat`.
4. Windows nhìn vào lúc này chỉ thấy một file vài chục GB mang tên `system_cache.dat`, không thể trích xuất nếu không có ứng dụng và mật khẩu.

## 3. Lựa chọn Công Nghệ

**Ngôn ngữ đề xuất:** `C# .NET` (WinForms hoặc WPF/MAUI cho giao diện đẹp).
*   **Thư viện:** 
    *   `System.Security.Cryptography.Aes` (cho phần mã hóa tiêu chuẩn quân đội).
    *   (Tùy chọn Cách 1B): `DokanNet` (.NET Wrapper cho Dokany) để mount thành ổ Z:\.
    *   (Tùy chọn Cách 1A): Thư viện mã nguồn mở `SharpZipLib` (với chế độ Store, không nén, có pass) hoặc tự custom luồng ghi tuần tự.

---
*Tài liệu này được tạo nhằm chốt phương pháp trước khi bắt tay vào triển khai Code thực tế.*
