/*
    EnterpriseRetailDemo.SchemaAndSeed.DBeaver.sql
    Run this file after selecting the EnterpriseRetailDemo database in DBeaver.
    This file creates 26 related tables and inserts exactly 100 rows into each table.
*/

IF DB_NAME() <> N'EnterpriseRetailDemo'
BEGIN
    THROW 51000, N'Select the EnterpriseRetailDemo database in DBeaver before running this file.', 1;
END;
SET NOCOUNT ON;
CREATE TABLE dbo.Countries
(
    CountryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Countries PRIMARY KEY,
    CountryCode CHAR(2) NOT NULL CONSTRAINT UQ_Countries_CountryCode UNIQUE,
    CountryName NVARCHAR(120) NOT NULL,
    Iso3Code CHAR(3) NOT NULL CONSTRAINT UQ_Countries_Iso3Code UNIQUE,
    PhonePrefix NVARCHAR(10) NOT NULL,
    CurrencyCode CHAR(3) NOT NULL,
    CurrencyName NVARCHAR(60) NOT NULL,
    TaxRate DECIMAL(5,2) NOT NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_Countries_IsActive DEFAULT (1),
    CreatedAt DATETIME2(0) NOT NULL
);
CREATE TABLE dbo.StatesProvinces
(
    StateID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StatesProvinces PRIMARY KEY,
    CountryID INT NOT NULL,
    StateCode NVARCHAR(10) NOT NULL CONSTRAINT UQ_StatesProvinces_StateCode UNIQUE,
    StateName NVARCHAR(120) NOT NULL,
    RegionType NVARCHAR(30) NOT NULL,
    PopulationEstimate BIGINT NOT NULL,
    AreaKm2 DECIMAL(12,2) NOT NULL,
    SalesTaxRate DECIMAL(5,2) NOT NULL,
    IsCoastal BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_StatesProvinces_Countries FOREIGN KEY (CountryID) REFERENCES dbo.Countries (CountryID)
);
CREATE TABLE dbo.Cities
(
    CityID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Cities PRIMARY KEY,
    StateID INT NOT NULL,
    CountryID INT NOT NULL,
    CityName NVARCHAR(120) NOT NULL,
    PostalCode NVARCHAR(20) NOT NULL,
    Latitude DECIMAL(9,6) NOT NULL,
    Longitude DECIMAL(9,6) NOT NULL,
    PopulationEstimate INT NOT NULL,
    TimeZoneName NVARCHAR(60) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT UQ_Cities_CityName UNIQUE (CityName),
    CONSTRAINT FK_Cities_StatesProvinces FOREIGN KEY (StateID) REFERENCES dbo.StatesProvinces (StateID),
    CONSTRAINT FK_Cities_Countries FOREIGN KEY (CountryID) REFERENCES dbo.Countries (CountryID)
);
CREATE TABLE dbo.Addresses
(
    AddressID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Addresses PRIMARY KEY,
    CountryID INT NOT NULL,
    StateID INT NOT NULL,
    CityID INT NOT NULL,
    AddressType NVARCHAR(30) NOT NULL,
    Line1 NVARCHAR(150) NOT NULL,
    Line2 NVARCHAR(150) NULL,
    PostalCode NVARCHAR(20) NOT NULL,
    District NVARCHAR(100) NOT NULL,
    BuildingNumber NVARCHAR(20) NOT NULL,
    Latitude DECIMAL(9,6) NULL,
    Longitude DECIMAL(9,6) NULL,
    IsPrimary BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Addresses_Countries FOREIGN KEY (CountryID) REFERENCES dbo.Countries (CountryID),
    CONSTRAINT FK_Addresses_StatesProvinces FOREIGN KEY (StateID) REFERENCES dbo.StatesProvinces (StateID),
    CONSTRAINT FK_Addresses_Cities FOREIGN KEY (CityID) REFERENCES dbo.Cities (CityID)
);
CREATE TABLE dbo.Departments
(
    DepartmentID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Departments PRIMARY KEY,
    ParentDepartmentID INT NULL,
    DepartmentCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Departments_DepartmentCode UNIQUE,
    DepartmentName NVARCHAR(120) NOT NULL,
    CostCenter NVARCHAR(20) NOT NULL,
    BudgetAmount DECIMAL(18,2) NOT NULL,
    PhoneExtension NVARCHAR(10) NOT NULL,
    IsOperational BIT NOT NULL,
    OpenedDate DATE NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Departments_ParentDepartment FOREIGN KEY (ParentDepartmentID) REFERENCES dbo.Departments (DepartmentID)
);
CREATE TABLE dbo.JobTitles
(
    JobTitleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobTitles PRIMARY KEY,
    DepartmentID INT NOT NULL,
    TitleCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_JobTitles_TitleCode UNIQUE,
    TitleName NVARCHAR(120) NOT NULL,
    GradeLevel TINYINT NOT NULL,
    MinSalary DECIMAL(18,2) NOT NULL,
    MaxSalary DECIMAL(18,2) NOT NULL,
    BonusRate DECIMAL(5,2) NOT NULL,
    IsManagerial BIT NOT NULL,
    RequiresCertification BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_JobTitles_Departments FOREIGN KEY (DepartmentID) REFERENCES dbo.Departments (DepartmentID)
);
CREATE TABLE dbo.Employees
(
    EmployeeID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
    DepartmentID INT NOT NULL,
    JobTitleID INT NOT NULL,
    AddressID INT NOT NULL,
    ManagerEmployeeID INT NULL,
    EmployeeCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Employees_EmployeeCode UNIQUE,
    FirstName NVARCHAR(60) NOT NULL,
    LastName NVARCHAR(60) NOT NULL,
    FullName AS (FirstName + N' ' + LastName) PERSISTED,
    Gender CHAR(1) NOT NULL,
    Email NVARCHAR(150) NOT NULL CONSTRAINT UQ_Employees_Email UNIQUE,
    PhoneNumber NVARCHAR(30) NOT NULL,
    HireDate DATE NOT NULL,
    BirthDate DATE NOT NULL,
    BaseSalary DECIMAL(18,2) NOT NULL,
    CommissionRate DECIMAL(5,2) NOT NULL,
    EmploymentStatus NVARCHAR(30) NOT NULL,
    IsActive BIT NOT NULL,
    LastLoginAt DATETIME2(0) NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Employees_Departments FOREIGN KEY (DepartmentID) REFERENCES dbo.Departments (DepartmentID),
    CONSTRAINT FK_Employees_JobTitles FOREIGN KEY (JobTitleID) REFERENCES dbo.JobTitles (JobTitleID),
    CONSTRAINT FK_Employees_Addresses FOREIGN KEY (AddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_Employees_Manager FOREIGN KEY (ManagerEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.Suppliers
(
    SupplierID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
    AddressID INT NOT NULL,
    PrimaryContactEmployeeID INT NOT NULL,
    SupplierCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Suppliers_SupplierCode UNIQUE,
    SupplierName NVARCHAR(150) NOT NULL,
    ContactName NVARCHAR(120) NOT NULL,
    ContactEmail NVARCHAR(150) NOT NULL CONSTRAINT UQ_Suppliers_ContactEmail UNIQUE,
    ContactPhone NVARCHAR(30) NOT NULL,
    SupplierType NVARCHAR(40) NOT NULL,
    CreditLimit DECIMAL(18,2) NOT NULL,
    PaymentTermsDays INT NOT NULL,
    Rating DECIMAL(3,2) NOT NULL,
    ActiveFrom DATE NOT NULL,
    IsPreferred BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Suppliers_Addresses FOREIGN KEY (AddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_Suppliers_Employees FOREIGN KEY (PrimaryContactEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.Warehouses
(
    WarehouseID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouses PRIMARY KEY,
    AddressID INT NOT NULL,
    ManagerEmployeeID INT NOT NULL,
    WarehouseCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Warehouses_WarehouseCode UNIQUE,
    WarehouseName NVARCHAR(150) NOT NULL,
    WarehouseType NVARCHAR(40) NOT NULL,
    CapacityUnits INT NOT NULL,
    SafetyStockThreshold INT NOT NULL,
    TemperatureControlled BIT NOT NULL,
    OperatingHours NVARCHAR(40) NOT NULL,
    IsActive BIT NOT NULL,
    OpenedDate DATE NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Warehouses_Addresses FOREIGN KEY (AddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_Warehouses_Employees FOREIGN KEY (ManagerEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.StoreLocations
(
    StoreLocationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoreLocations PRIMARY KEY,
    AddressID INT NOT NULL,
    ManagerEmployeeID INT NOT NULL,
    StoreCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_StoreLocations_StoreCode UNIQUE,
    StoreName NVARCHAR(150) NOT NULL,
    StoreFormat NVARCHAR(40) NOT NULL,
    FloorAreaSqm DECIMAL(12,2) NOT NULL,
    DailyVisitorCapacity INT NOT NULL,
    OpenTime TIME(0) NOT NULL,
    CloseTime TIME(0) NOT NULL,
    OpenedDate DATE NOT NULL,
    IsFlagship BIT NOT NULL,
    IsActive BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_StoreLocations_Addresses FOREIGN KEY (AddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_StoreLocations_Employees FOREIGN KEY (ManagerEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.Customers
(
    CustomerID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    BillingAddressID INT NOT NULL,
    ShippingAddressID INT NOT NULL,
    AccountManagerEmployeeID INT NOT NULL,
    CustomerCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Customers_CustomerCode UNIQUE,
    CustomerGuid UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Customers_CustomerGuid DEFAULT NEWID(),
    FirstName NVARCHAR(60) NOT NULL,
    LastName NVARCHAR(60) NOT NULL,
    FullName AS (FirstName + N' ' + LastName) PERSISTED,
    Email NVARCHAR(150) NOT NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    PhoneNumber NVARCHAR(30) NOT NULL,
    BirthDate DATE NOT NULL,
    Gender CHAR(1) NOT NULL,
    CustomerTier NVARCHAR(20) NOT NULL,
    LoyaltyPoints INT NOT NULL,
    CreditScore SMALLINT NOT NULL,
    MarketingOptIn BIT NOT NULL,
    RegisteredAt DATETIME2(0) NOT NULL,
    IsActive BIT NOT NULL,
    Notes NVARCHAR(250) NULL,
    CONSTRAINT FK_Customers_BillingAddress FOREIGN KEY (BillingAddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_Customers_ShippingAddress FOREIGN KEY (ShippingAddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_Customers_Employees FOREIGN KEY (AccountManagerEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.Categories
(
    CategoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
    ParentCategoryID INT NULL,
    CategoryCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Categories_CategoryCode UNIQUE,
    CategoryName NVARCHAR(120) NOT NULL,
    CategoryLevel TINYINT NOT NULL,
    DisplayOrder INT NOT NULL,
    CommissionRate DECIMAL(5,2) NOT NULL,
    SeoSlug NVARCHAR(120) NOT NULL CONSTRAINT UQ_Categories_SeoSlug UNIQUE,
    IsSeasonal BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Categories_ParentCategory FOREIGN KEY (ParentCategoryID) REFERENCES dbo.Categories (CategoryID)
);
CREATE TABLE dbo.Brands
(
    BrandID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Brands PRIMARY KEY,
    CountryID INT NOT NULL,
    BrandCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Brands_BrandCode UNIQUE,
    BrandName NVARCHAR(150) NOT NULL,
    FoundedYear SMALLINT NOT NULL,
    BrandType NVARCHAR(40) NOT NULL,
    WebsiteUrl NVARCHAR(200) NOT NULL,
    SupportEmail NVARCHAR(150) NOT NULL CONSTRAINT UQ_Brands_SupportEmail UNIQUE,
    ReputationScore DECIMAL(5,2) NOT NULL,
    IsPrivateLabel BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Brands_Countries FOREIGN KEY (CountryID) REFERENCES dbo.Countries (CountryID)
);
CREATE TABLE dbo.Products
(
    ProductID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    CategoryID INT NOT NULL,
    BrandID INT NOT NULL,
    SupplierID INT NOT NULL,
    CreatedByEmployeeID INT NOT NULL,
    ProductCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_Products_ProductCode UNIQUE,
    ProductGuid UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Products_ProductGuid DEFAULT NEWID(),
    ProductName NVARCHAR(180) NOT NULL,
    SKURoot NVARCHAR(40) NOT NULL CONSTRAINT UQ_Products_SKURoot UNIQUE,
    ProductType NVARCHAR(40) NOT NULL,
    UnitOfMeasure NVARCHAR(20) NOT NULL,
    WeightKg DECIMAL(10,2) NOT NULL,
    ShelfLifeDays INT NOT NULL,
    ReorderLevel INT NOT NULL,
    StandardCost DECIMAL(18,2) NOT NULL,
    Description NVARCHAR(250) NOT NULL,
    AttributesJson NVARCHAR(MAX) NULL,
    IsDiscontinued BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories (CategoryID),
    CONSTRAINT FK_Products_Brands FOREIGN KEY (BrandID) REFERENCES dbo.Brands (BrandID),
    CONSTRAINT FK_Products_Suppliers FOREIGN KEY (SupplierID) REFERENCES dbo.Suppliers (SupplierID),
    CONSTRAINT FK_Products_Employees FOREIGN KEY (CreatedByEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.ProductVariants
(
    ProductVariantID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductVariants PRIMARY KEY,
    ProductID INT NOT NULL,
    VariantCode NVARCHAR(20) NOT NULL CONSTRAINT UQ_ProductVariants_VariantCode UNIQUE,
    VariantName NVARCHAR(150) NOT NULL,
    ColorName NVARCHAR(40) NOT NULL,
    SizeLabel NVARCHAR(20) NOT NULL,
    Barcode NVARCHAR(30) NOT NULL CONSTRAINT UQ_ProductVariants_Barcode UNIQUE,
    UnitVolumeCm3 DECIMAL(12,2) NOT NULL,
    UnitWeightKg DECIMAL(10,2) NOT NULL,
    MSRP DECIMAL(18,2) NOT NULL,
    IsDefaultVariant BIT NOT NULL,
    IsActive BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_ProductVariants_Products FOREIGN KEY (ProductID) REFERENCES dbo.Products (ProductID)
);
CREATE TABLE dbo.ProductPrices
(
    ProductPriceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductPrices PRIMARY KEY,
    ProductVariantID INT NOT NULL,
    ApprovedByEmployeeID INT NOT NULL,
    PriceType NVARCHAR(30) NOT NULL,
    CurrencyCode CHAR(3) NOT NULL,
    PriceAmount DECIMAL(18,2) NOT NULL,
    CostAmount DECIMAL(18,2) NOT NULL,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    DiscountPercent DECIMAL(5,2) NOT NULL,
    TaxIncluded BIT NOT NULL,
    PriceSource NVARCHAR(40) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_ProductPrices_ProductVariants FOREIGN KEY (ProductVariantID) REFERENCES dbo.ProductVariants (ProductVariantID),
    CONSTRAINT FK_ProductPrices_Employees FOREIGN KEY (ApprovedByEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.InventoryBalances
(
    InventoryBalanceID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryBalances PRIMARY KEY,
    WarehouseID INT NOT NULL,
    ProductVariantID INT NOT NULL,
    QuantityOnHand INT NOT NULL,
    QuantityReserved INT NOT NULL,
    QuantityAvailable AS (QuantityOnHand - QuantityReserved) PERSISTED,
    QuantityInTransit INT NOT NULL,
    AverageUnitCost DECIMAL(18,2) NOT NULL,
    LastCountedAt DATETIME2(0) NOT NULL,
    LotNumber NVARCHAR(40) NOT NULL,
    ExpirationDate DATE NULL,
    IsBlocked BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT UQ_InventoryBalances_WarehouseVariant UNIQUE (WarehouseID, ProductVariantID),
    CONSTRAINT FK_InventoryBalances_Warehouses FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses (WarehouseID),
    CONSTRAINT FK_InventoryBalances_ProductVariants FOREIGN KEY (ProductVariantID) REFERENCES dbo.ProductVariants (ProductVariantID)
);
CREATE TABLE dbo.PurchaseOrders
(
    PurchaseOrderID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrders PRIMARY KEY,
    SupplierID INT NOT NULL,
    WarehouseID INT NOT NULL,
    RequestedByEmployeeID INT NOT NULL,
    ApprovedByEmployeeID INT NOT NULL,
    PurchaseOrderNumber NVARCHAR(30) NOT NULL CONSTRAINT UQ_PurchaseOrders_PurchaseOrderNumber UNIQUE,
    OrderDate DATE NOT NULL,
    ExpectedDate DATE NOT NULL,
    ReceivedDate DATE NULL,
    OrderStatus NVARCHAR(30) NOT NULL,
    SubtotalAmount DECIMAL(18,2) NOT NULL,
    TaxAmount DECIMAL(18,2) NOT NULL,
    FreightAmount DECIMAL(18,2) NOT NULL,
    DiscountAmount DECIMAL(18,2) NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL,
    PaymentStatus NVARCHAR(30) NOT NULL,
    Notes NVARCHAR(250) NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_PurchaseOrders_Suppliers FOREIGN KEY (SupplierID) REFERENCES dbo.Suppliers (SupplierID),
    CONSTRAINT FK_PurchaseOrders_Warehouses FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses (WarehouseID),
    CONSTRAINT FK_PurchaseOrders_RequestedBy FOREIGN KEY (RequestedByEmployeeID) REFERENCES dbo.Employees (EmployeeID),
    CONSTRAINT FK_PurchaseOrders_ApprovedBy FOREIGN KEY (ApprovedByEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.PurchaseOrderLines
(
    PurchaseOrderLineID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrderLines PRIMARY KEY,
    PurchaseOrderID INT NOT NULL,
    ProductVariantID INT NOT NULL,
    OrderedQty INT NOT NULL,
    ReceivedQty INT NOT NULL,
    RejectedQty INT NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(5,2) NOT NULL,
    TaxRate DECIMAL(5,2) NOT NULL,
    LineTotal DECIMAL(18,2) NOT NULL,
    ExpectedReceiptDate DATE NOT NULL,
    QualityStatus NVARCHAR(30) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_PurchaseOrderLines_PurchaseOrders FOREIGN KEY (PurchaseOrderID) REFERENCES dbo.PurchaseOrders (PurchaseOrderID),
    CONSTRAINT FK_PurchaseOrderLines_ProductVariants FOREIGN KEY (ProductVariantID) REFERENCES dbo.ProductVariants (ProductVariantID)
);
CREATE TABLE dbo.SalesOrders
(
    SalesOrderID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOrders PRIMARY KEY,
    CustomerID INT NOT NULL,
    StoreLocationID INT NOT NULL,
    SalesRepEmployeeID INT NOT NULL,
    BillingAddressID INT NOT NULL,
    ShippingAddressID INT NOT NULL,
    SalesOrderNumber NVARCHAR(30) NOT NULL CONSTRAINT UQ_SalesOrders_SalesOrderNumber UNIQUE,
    SalesOrderGuid UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SalesOrders_SalesOrderGuid DEFAULT NEWID(),
    OrderDate DATETIME2(0) NOT NULL,
    RequiredDate DATE NOT NULL,
    ShippedDate DATE NULL,
    OrderStatus NVARCHAR(30) NOT NULL,
    ChannelName NVARCHAR(30) NOT NULL,
    SubtotalAmount DECIMAL(18,2) NOT NULL,
    DiscountAmount DECIMAL(18,2) NOT NULL,
    TaxAmount DECIMAL(18,2) NOT NULL,
    ShippingAmount DECIMAL(18,2) NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL,
    PriorityLevel TINYINT NOT NULL,
    CustomerNote NVARCHAR(250) NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_SalesOrders_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
    CONSTRAINT FK_SalesOrders_StoreLocations FOREIGN KEY (StoreLocationID) REFERENCES dbo.StoreLocations (StoreLocationID),
    CONSTRAINT FK_SalesOrders_Employees FOREIGN KEY (SalesRepEmployeeID) REFERENCES dbo.Employees (EmployeeID),
    CONSTRAINT FK_SalesOrders_BillingAddress FOREIGN KEY (BillingAddressID) REFERENCES dbo.Addresses (AddressID),
    CONSTRAINT FK_SalesOrders_ShippingAddress FOREIGN KEY (ShippingAddressID) REFERENCES dbo.Addresses (AddressID)
);
CREATE TABLE dbo.SalesOrderLines
(
    SalesOrderLineID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesOrderLines PRIMARY KEY,
    SalesOrderID INT NOT NULL,
    ProductVariantID INT NOT NULL,
    QuantityOrdered INT NOT NULL,
    QuantityShipped INT NOT NULL,
    QuantityReturned INT NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(5,2) NOT NULL,
    TaxRate DECIMAL(5,2) NOT NULL,
    LineTotal DECIMAL(18,2) NOT NULL,
    FulfillmentStatus NVARCHAR(30) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_SalesOrderLines_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID),
    CONSTRAINT FK_SalesOrderLines_ProductVariants FOREIGN KEY (ProductVariantID) REFERENCES dbo.ProductVariants (ProductVariantID)
);
CREATE TABLE dbo.Payments
(
    PaymentID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    SalesOrderID INT NOT NULL,
    CustomerID INT NOT NULL,
    ProcessedByEmployeeID INT NOT NULL,
    PaymentReference NVARCHAR(30) NOT NULL CONSTRAINT UQ_Payments_PaymentReference UNIQUE,
    PaymentDate DATETIME2(0) NOT NULL,
    PaymentMethod NVARCHAR(30) NOT NULL,
    PaymentChannel NVARCHAR(30) NOT NULL,
    CurrencyCode CHAR(3) NOT NULL,
    AmountPaid DECIMAL(18,2) NOT NULL,
    GatewayFee DECIMAL(18,2) NOT NULL,
    PaymentStatus NVARCHAR(30) NOT NULL,
    AuthorizationCode NVARCHAR(50) NOT NULL CONSTRAINT UQ_Payments_AuthorizationCode UNIQUE,
    SettlementDate DATE NULL,
    RefundAmount DECIMAL(18,2) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Payments_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID),
    CONSTRAINT FK_Payments_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
    CONSTRAINT FK_Payments_Employees FOREIGN KEY (ProcessedByEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.Shipments
(
    ShipmentID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shipments PRIMARY KEY,
    SalesOrderID INT NOT NULL,
    WarehouseID INT NOT NULL,
    StoreLocationID INT NOT NULL,
    CarrierName NVARCHAR(80) NOT NULL,
    ServiceLevel NVARCHAR(30) NOT NULL,
    TrackingNumber NVARCHAR(40) NOT NULL CONSTRAINT UQ_Shipments_TrackingNumber UNIQUE,
    ShippedDate DATETIME2(0) NOT NULL,
    DeliveredDate DATETIME2(0) NULL,
    ShipmentStatus NVARCHAR(30) NOT NULL,
    PackageCount INT NOT NULL,
    TotalWeightKg DECIMAL(10,2) NOT NULL,
    ShippingCost DECIMAL(18,2) NOT NULL,
    SignatureRequired BIT NOT NULL,
    RecipientName NVARCHAR(120) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Shipments_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID),
    CONSTRAINT FK_Shipments_Warehouses FOREIGN KEY (WarehouseID) REFERENCES dbo.Warehouses (WarehouseID),
    CONSTRAINT FK_Shipments_StoreLocations FOREIGN KEY (StoreLocationID) REFERENCES dbo.StoreLocations (StoreLocationID)
);
CREATE TABLE dbo.Reviews
(
    ReviewID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Reviews PRIMARY KEY,
    CustomerID INT NOT NULL,
    ProductID INT NOT NULL,
    SalesOrderID INT NOT NULL,
    Rating TINYINT NOT NULL,
    ReviewTitle NVARCHAR(120) NOT NULL,
    ReviewText NVARCHAR(400) NOT NULL,
    ReviewStatus NVARCHAR(30) NOT NULL,
    HelpfulVotes INT NOT NULL,
    UnhelpfulVotes INT NOT NULL,
    WouldRecommend BIT NOT NULL,
    SubmittedAt DATETIME2(0) NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_Reviews_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
    CONSTRAINT FK_Reviews_Products FOREIGN KEY (ProductID) REFERENCES dbo.Products (ProductID),
    CONSTRAINT FK_Reviews_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID)
);
CREATE TABLE dbo.SupportTickets
(
    SupportTicketID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportTickets PRIMARY KEY,
    CustomerID INT NOT NULL,
    SalesOrderID INT NOT NULL,
    AssignedEmployeeID INT NOT NULL,
    TicketNumber NVARCHAR(30) NOT NULL CONSTRAINT UQ_SupportTickets_TicketNumber UNIQUE,
    TicketType NVARCHAR(40) NOT NULL,
    Subject NVARCHAR(150) NOT NULL,
    Description NVARCHAR(400) NOT NULL,
    PriorityName NVARCHAR(20) NOT NULL,
    TicketStatus NVARCHAR(30) NOT NULL,
    OpenedAt DATETIME2(0) NOT NULL,
    FirstResponseAt DATETIME2(0) NULL,
    ClosedAt DATETIME2(0) NULL,
    SatisfactionScore TINYINT NULL,
    ResolutionSummary NVARCHAR(250) NULL,
    TagsJson NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_SupportTickets_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
    CONSTRAINT FK_SupportTickets_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID),
    CONSTRAINT FK_SupportTickets_Employees FOREIGN KEY (AssignedEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE TABLE dbo.ReturnRequests
(
    ReturnRequestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReturnRequests PRIMARY KEY,
    SalesOrderID INT NOT NULL,
    SalesOrderLineID INT NOT NULL,
    CustomerID INT NOT NULL,
    ApprovedByEmployeeID INT NULL,
    ReturnNumber NVARCHAR(30) NOT NULL CONSTRAINT UQ_ReturnRequests_ReturnNumber UNIQUE,
    RequestDate DATETIME2(0) NOT NULL,
    ReasonCode NVARCHAR(30) NOT NULL,
    ReasonDescription NVARCHAR(250) NOT NULL,
    QuantityRequested INT NOT NULL,
    QuantityApproved INT NOT NULL,
    RefundAmount DECIMAL(18,2) NOT NULL,
    ReturnStatus NVARCHAR(30) NOT NULL,
    ReceivedAt DATETIME2(0) NULL,
    Restockable BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL,
    CONSTRAINT FK_ReturnRequests_SalesOrders FOREIGN KEY (SalesOrderID) REFERENCES dbo.SalesOrders (SalesOrderID),
    CONSTRAINT FK_ReturnRequests_SalesOrderLines FOREIGN KEY (SalesOrderLineID) REFERENCES dbo.SalesOrderLines (SalesOrderLineID),
    CONSTRAINT FK_ReturnRequests_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
    CONSTRAINT FK_ReturnRequests_Employees FOREIGN KEY (ApprovedByEmployeeID) REFERENCES dbo.Employees (EmployeeID)
);
CREATE INDEX IX_StatesProvinces_CountryID ON dbo.StatesProvinces (CountryID);
CREATE INDEX IX_Cities_StateID ON dbo.Cities (StateID);
CREATE INDEX IX_Addresses_CityID ON dbo.Addresses (CityID);
CREATE INDEX IX_Employees_DepartmentID ON dbo.Employees (DepartmentID);
CREATE INDEX IX_Employees_ManagerEmployeeID ON dbo.Employees (ManagerEmployeeID);
CREATE INDEX IX_Suppliers_AddressID ON dbo.Suppliers (AddressID);
CREATE INDEX IX_Warehouses_ManagerEmployeeID ON dbo.Warehouses (ManagerEmployeeID);
CREATE INDEX IX_StoreLocations_ManagerEmployeeID ON dbo.StoreLocations (ManagerEmployeeID);
CREATE INDEX IX_Customers_AccountManagerEmployeeID ON dbo.Customers (AccountManagerEmployeeID);
CREATE INDEX IX_Products_CategoryID ON dbo.Products (CategoryID);
CREATE INDEX IX_Products_SupplierID ON dbo.Products (SupplierID);
CREATE INDEX IX_ProductVariants_ProductID ON dbo.ProductVariants (ProductID);
CREATE INDEX IX_ProductPrices_ProductVariantID ON dbo.ProductPrices (ProductVariantID);
CREATE INDEX IX_PurchaseOrders_SupplierID_OrderDate ON dbo.PurchaseOrders (SupplierID, OrderDate);
CREATE INDEX IX_PurchaseOrderLines_PurchaseOrderID ON dbo.PurchaseOrderLines (PurchaseOrderID);
CREATE INDEX IX_SalesOrders_CustomerID_OrderDate ON dbo.SalesOrders (CustomerID, OrderDate);
CREATE INDEX IX_SalesOrderLines_SalesOrderID ON dbo.SalesOrderLines (SalesOrderID);
CREATE INDEX IX_Payments_SalesOrderID ON dbo.Payments (SalesOrderID);
CREATE INDEX IX_Shipments_SalesOrderID ON dbo.Shipments (SalesOrderID);
CREATE INDEX IX_Reviews_ProductID ON dbo.Reviews (ProductID);
CREATE INDEX IX_SupportTickets_CustomerID ON dbo.SupportTickets (CustomerID);
CREATE INDEX IX_ReturnRequests_SalesOrderID ON dbo.ReturnRequests (SalesOrderID);
IF OBJECT_ID(N'dbo.__SeedNumbers', N'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.__SeedNumbers;
END;

CREATE TABLE dbo.__SeedNumbers
(
    n INT NOT NULL CONSTRAINT PK___SeedNumbers PRIMARY KEY CLUSTERED
);

WITH NumberSeries AS
(
    SELECT 1 AS n
    UNION ALL
    SELECT n + 1
    FROM NumberSeries
    WHERE n < 100
)
INSERT INTO dbo.__SeedNumbers (n)
SELECT n
FROM NumberSeries
OPTION (MAXRECURSION 100);

    SET IDENTITY_INSERT dbo.Countries ON;

    INSERT INTO dbo.Countries
    (
        CountryID,
        CountryCode,
        CountryName,
        Iso3Code,
        PhonePrefix,
        CurrencyCode,
        CurrencyName,
        TaxRate,
        IsActive,
        CreatedAt
    )
    SELECT
        n,
        CONCAT(CHAR(65 + ((n - 1) / 26)), CHAR(65 + ((n - 1) % 26))),
        CONCAT(N'Country ', n),
        CONCAT(N'X', CHAR(65 + ((n - 1) / 26)), CHAR(65 + ((n - 1) % 26))),
        CONCAT(N'+', 800 + n),
        CONCAT(N'C', CHAR(65 + ((n - 1) / 26)), CHAR(65 + ((n - 1) % 26))),
        CONCAT(N'Currency ', n),
        CAST(5.00 + ((n % 12) * 0.75) AS DECIMAL(5,2)),
        CASE WHEN n % 15 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2023-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Countries OFF;

    SET IDENTITY_INSERT dbo.StatesProvinces ON;

    INSERT INTO dbo.StatesProvinces
    (
        StateID,
        CountryID,
        StateCode,
        StateName,
        RegionType,
        PopulationEstimate,
        AreaKm2,
        SalesTaxRate,
        IsCoastal,
        CreatedAt
    )
    SELECT
        n,
        n,
        CONCAT(N'ST', RIGHT(CONCAT(N'000', n), 3)),
        CONCAT(N'State ', n),
        CASE n % 4
            WHEN 0 THEN N'State'
            WHEN 1 THEN N'Province'
            WHEN 2 THEN N'Region'
            ELSE N'Territory'
        END,
        CAST(250000 + (n * 35000) AS BIGINT),
        CAST(1500.00 + (n * 17.25) AS DECIMAL(12,2)),
        CAST(4.50 + ((n % 8) * 0.65) AS DECIMAL(5,2)),
        CASE WHEN n % 3 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2023-02-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.StatesProvinces OFF;

    SET IDENTITY_INSERT dbo.Cities ON;

    INSERT INTO dbo.Cities
    (
        CityID,
        StateID,
        CountryID,
        CityName,
        PostalCode,
        Latitude,
        Longitude,
        PopulationEstimate,
        TimeZoneName,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        CONCAT(N'City ', n),
        RIGHT(CONCAT(N'00000', 10000 + n), 5),
        CAST(-12.000000 + (n * 0.420000) AS DECIMAL(9,6)),
        CAST(30.000000 + (n * 0.550000) AS DECIMAL(9,6)),
        50000 + (n * 7000),
        CASE n % 5
            WHEN 0 THEN N'UTC-5'
            WHEN 1 THEN N'UTC+0'
            WHEN 2 THEN N'UTC+1'
            WHEN 3 THEN N'UTC+7'
            ELSE N'UTC+9'
        END,
        DATEADD(DAY, n, CAST('2023-03-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Cities OFF;

    SET IDENTITY_INSERT dbo.Addresses ON;

    INSERT INTO dbo.Addresses
    (
        AddressID,
        CountryID,
        StateID,
        CityID,
        AddressType,
        Line1,
        Line2,
        PostalCode,
        District,
        BuildingNumber,
        Latitude,
        Longitude,
        IsPrimary,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        n,
        CASE n % 4
            WHEN 0 THEN N'Billing'
            WHEN 1 THEN N'Shipping'
            WHEN 2 THEN N'Warehouse'
            ELSE N'Office'
        END,
        CONCAT(N'Street ', n, N' Avenue'),
        CASE WHEN n % 3 = 0 THEN CONCAT(N'Suite ', 100 + n) ELSE NULL END,
        RIGHT(CONCAT(N'00000', 10000 + n), 5),
        CONCAT(N'District ', ((n - 1) % 20) + 1),
        CONCAT(N'B-', RIGHT(CONCAT(N'000', n), 3)),
        CAST(-11.800000 + (n * 0.420000) AS DECIMAL(9,6)),
        CAST(30.200000 + (n * 0.550000) AS DECIMAL(9,6)),
        CASE WHEN n % 10 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2023-04-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Addresses OFF;

    SET IDENTITY_INSERT dbo.Departments ON;

    INSERT INTO dbo.Departments
    (
        DepartmentID,
        ParentDepartmentID,
        DepartmentCode,
        DepartmentName,
        CostCenter,
        BudgetAmount,
        PhoneExtension,
        IsOperational,
        OpenedDate,
        CreatedAt
    )
    SELECT
        n,
        NULL,
        CONCAT(N'DEP', RIGHT(CONCAT(N'000', n), 3)),
        CONCAT(N'Department ', n),
        CONCAT(N'CC', RIGHT(CONCAT(N'0000', n), 4)),
        CAST(100000.00 + (n * 8500.00) AS DECIMAL(18,2)),
        RIGHT(CONCAT(N'0000', 2000 + n), 4),
        CASE WHEN n % 12 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2018-01-01' AS DATE)),
        DATEADD(DAY, n, CAST('2018-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 1 AND 10
    ORDER BY n;

    INSERT INTO dbo.Departments
    (
        DepartmentID,
        ParentDepartmentID,
        DepartmentCode,
        DepartmentName,
        CostCenter,
        BudgetAmount,
        PhoneExtension,
        IsOperational,
        OpenedDate,
        CreatedAt
    )
    SELECT
        n,
        ((n - 1) % 10) + 1,
        CONCAT(N'DEP', RIGHT(CONCAT(N'000', n), 3)),
        CONCAT(N'Department ', n),
        CONCAT(N'CC', RIGHT(CONCAT(N'0000', n), 4)),
        CAST(100000.00 + (n * 8500.00) AS DECIMAL(18,2)),
        RIGHT(CONCAT(N'0000', 2000 + n), 4),
        CASE WHEN n % 12 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2018-01-01' AS DATE)),
        DATEADD(DAY, n, CAST('2018-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 11 AND 100
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Departments OFF;

    SET IDENTITY_INSERT dbo.JobTitles ON;

    INSERT INTO dbo.JobTitles
    (
        JobTitleID,
        DepartmentID,
        TitleCode,
        TitleName,
        GradeLevel,
        MinSalary,
        MaxSalary,
        BonusRate,
        IsManagerial,
        RequiresCertification,
        CreatedAt
    )
    SELECT
        n,
        n,
        CONCAT(N'JOB', RIGHT(CONCAT(N'000', n), 3)),
        CONCAT(N'Job Title ', n),
        ((n - 1) % 10) + 1,
        CAST(32000.00 + (n * 450.00) AS DECIMAL(18,2)),
        CAST(47000.00 + (n * 620.00) AS DECIMAL(18,2)),
        CAST((n % 12) * 0.50 AS DECIMAL(5,2)),
        CASE WHEN n % 8 IN (0, 1) THEN 1 ELSE 0 END,
        CASE WHEN n % 3 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2018-02-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.JobTitles OFF;

    SET IDENTITY_INSERT dbo.Employees ON;

    INSERT INTO dbo.Employees
    (
        EmployeeID,
        DepartmentID,
        JobTitleID,
        AddressID,
        ManagerEmployeeID,
        EmployeeCode,
        FirstName,
        LastName,
        Gender,
        Email,
        PhoneNumber,
        HireDate,
        BirthDate,
        BaseSalary,
        CommissionRate,
        EmploymentStatus,
        IsActive,
        LastLoginAt,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        n,
        NULL,
        CONCAT(N'EMP', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'First', n),
        CONCAT(N'Last', n),
        CASE WHEN n % 2 = 0 THEN 'F' ELSE 'M' END,
        CONCAT(N'employee', RIGHT(CONCAT(N'000', n), 3), N'@demo.local'),
        CONCAT(N'+1-555-', RIGHT(CONCAT(N'0000', 3000 + n), 4)),
        DATEADD(DAY, n * 11, CAST('2017-01-01' AS DATE)),
        DATEADD(DAY, n * 50, CAST('1980-01-01' AS DATE)),
        CAST(38000.00 + (n * 950.00) AS DECIMAL(18,2)),
        CAST((n % 7) * 0.75 AS DECIMAL(5,2)),
        CASE n % 4
            WHEN 0 THEN N'Active'
            WHEN 1 THEN N'Onboarding'
            WHEN 2 THEN N'OnLeave'
            ELSE N'Probation'
        END,
        CASE WHEN n % 14 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2026-01-01' AS DATETIME2(0))),
        DATEADD(DAY, n, CAST('2017-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 1 AND 10
    ORDER BY n;

    INSERT INTO dbo.Employees
    (
        EmployeeID,
        DepartmentID,
        JobTitleID,
        AddressID,
        ManagerEmployeeID,
        EmployeeCode,
        FirstName,
        LastName,
        Gender,
        Email,
        PhoneNumber,
        HireDate,
        BirthDate,
        BaseSalary,
        CommissionRate,
        EmploymentStatus,
        IsActive,
        LastLoginAt,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        n,
        ((n - 1) % 10) + 1,
        CONCAT(N'EMP', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'First', n),
        CONCAT(N'Last', n),
        CASE WHEN n % 2 = 0 THEN 'F' ELSE 'M' END,
        CONCAT(N'employee', RIGHT(CONCAT(N'000', n), 3), N'@demo.local'),
        CONCAT(N'+1-555-', RIGHT(CONCAT(N'0000', 3000 + n), 4)),
        DATEADD(DAY, n * 11, CAST('2017-01-01' AS DATE)),
        DATEADD(DAY, n * 50, CAST('1980-01-01' AS DATE)),
        CAST(38000.00 + (n * 950.00) AS DECIMAL(18,2)),
        CAST((n % 7) * 0.75 AS DECIMAL(5,2)),
        CASE n % 4
            WHEN 0 THEN N'Active'
            WHEN 1 THEN N'Onboarding'
            WHEN 2 THEN N'OnLeave'
            ELSE N'Probation'
        END,
        CASE WHEN n % 14 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2026-01-01' AS DATETIME2(0))),
        DATEADD(DAY, n, CAST('2017-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 11 AND 100
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Employees OFF;

    SET IDENTITY_INSERT dbo.Suppliers ON;

    INSERT INTO dbo.Suppliers
    (
        SupplierID,
        AddressID,
        PrimaryContactEmployeeID,
        SupplierCode,
        SupplierName,
        ContactName,
        ContactEmail,
        ContactPhone,
        SupplierType,
        CreditLimit,
        PaymentTermsDays,
        Rating,
        ActiveFrom,
        IsPreferred,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 9) % 100) + 1,
        CONCAT(N'SUP', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Supplier ', n),
        CONCAT(N'Contact ', n),
        CONCAT(N'supplier', RIGHT(CONCAT(N'000', n), 3), N'@demo.local'),
        CONCAT(N'+1-666-', RIGHT(CONCAT(N'0000', 4000 + n), 4)),
        CASE n % 4
            WHEN 0 THEN N'Manufacturer'
            WHEN 1 THEN N'Distributor'
            WHEN 2 THEN N'Wholesaler'
            ELSE N'Importer'
        END,
        CAST(15000.00 + (n * 750.00) AS DECIMAL(18,2)),
        CASE n % 4
            WHEN 0 THEN 15
            WHEN 1 THEN 30
            WHEN 2 THEN 45
            ELSE 60
        END,
        CAST(2.50 + ((n % 25) * 0.10) AS DECIMAL(3,2)),
        DATEADD(DAY, n * 2, CAST('2019-01-01' AS DATE)),
        CASE WHEN n % 5 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2019-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Suppliers OFF;

    SET IDENTITY_INSERT dbo.Warehouses ON;

    INSERT INTO dbo.Warehouses
    (
        WarehouseID,
        AddressID,
        ManagerEmployeeID,
        WarehouseCode,
        WarehouseName,
        WarehouseType,
        CapacityUnits,
        SafetyStockThreshold,
        TemperatureControlled,
        OperatingHours,
        IsActive,
        OpenedDate,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        CONCAT(N'WH', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Warehouse ', n),
        CASE n % 3
            WHEN 0 THEN N'Regional'
            WHEN 1 THEN N'ColdStorage'
            ELSE N'Fulfillment'
        END,
        5000 + (n * 120),
        50 + n,
        CASE WHEN n % 2 = 0 THEN 1 ELSE 0 END,
        N'06:00-22:00',
        CASE WHEN n % 18 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n * 3, CAST('2016-01-01' AS DATE)),
        DATEADD(DAY, n, CAST('2016-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Warehouses OFF;

    SET IDENTITY_INSERT dbo.StoreLocations ON;

    INSERT INTO dbo.StoreLocations
    (
        StoreLocationID,
        AddressID,
        ManagerEmployeeID,
        StoreCode,
        StoreName,
        StoreFormat,
        FloorAreaSqm,
        DailyVisitorCapacity,
        OpenTime,
        CloseTime,
        OpenedDate,
        IsFlagship,
        IsActive,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 14) % 100) + 1,
        CONCAT(N'STR', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Store ', n),
        CASE n % 4
            WHEN 0 THEN N'Mall'
            WHEN 1 THEN N'Street'
            WHEN 2 THEN N'Airport'
            ELSE N'Outlet'
        END,
        CAST(180.00 + (n * 9.50) AS DECIMAL(12,2)),
        300 + (n * 20),
        CAST('08:00:00' AS TIME(0)),
        CAST('22:00:00' AS TIME(0)),
        DATEADD(DAY, n * 4, CAST('2016-06-01' AS DATE)),
        CASE WHEN n % 20 = 0 THEN 1 ELSE 0 END,
        CASE WHEN n % 17 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2016-06-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.StoreLocations OFF;

    SET IDENTITY_INSERT dbo.Customers ON;

    INSERT INTO dbo.Customers
    (
        CustomerID,
        BillingAddressID,
        ShippingAddressID,
        AccountManagerEmployeeID,
        CustomerCode,
        FirstName,
        LastName,
        Email,
        PhoneNumber,
        BirthDate,
        Gender,
        CustomerTier,
        LoyaltyPoints,
        CreditScore,
        MarketingOptIn,
        RegisteredAt,
        IsActive,
        Notes
    )
    SELECT
        n,
        n,
        (n % 100) + 1,
        ((n + 29) % 100) + 1,
        CONCAT(N'CUS', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'CustomerFirst', n),
        CONCAT(N'CustomerLast', n),
        CONCAT(N'customer', RIGHT(CONCAT(N'000', n), 3), N'@demo.local'),
        CONCAT(N'+1-777-', RIGHT(CONCAT(N'0000', 5000 + n), 4)),
        DATEADD(DAY, n * 42, CAST('1985-01-01' AS DATE)),
        CASE WHEN n % 2 = 0 THEN 'F' ELSE 'M' END,
        CASE n % 4
            WHEN 0 THEN N'Bronze'
            WHEN 1 THEN N'Silver'
            WHEN 2 THEN N'Gold'
            ELSE N'Platinum'
        END,
        n * 120,
        600 + (n % 151),
        CASE WHEN n % 2 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2021-01-01' AS DATETIME2(0))),
        CASE WHEN n % 19 = 0 THEN 0 ELSE 1 END,
        CONCAT(N'Customer note ', n)
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Customers OFF;

    SET IDENTITY_INSERT dbo.Categories ON;

    INSERT INTO dbo.Categories
    (
        CategoryID,
        ParentCategoryID,
        CategoryCode,
        CategoryName,
        CategoryLevel,
        DisplayOrder,
        CommissionRate,
        SeoSlug,
        IsSeasonal,
        CreatedAt
    )
    SELECT
        n,
        NULL,
        CONCAT(N'CAT', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Category ', n),
        1,
        n,
        CAST(2.00 + ((n % 9) * 0.50) AS DECIMAL(5,2)),
        CONCAT(N'category-', n),
        CASE WHEN n % 6 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2020-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 1 AND 20
    ORDER BY n;

    INSERT INTO dbo.Categories
    (
        CategoryID,
        ParentCategoryID,
        CategoryCode,
        CategoryName,
        CategoryLevel,
        DisplayOrder,
        CommissionRate,
        SeoSlug,
        IsSeasonal,
        CreatedAt
    )
    SELECT
        n,
        ((n - 1) % 20) + 1,
        CONCAT(N'CAT', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Category ', n),
        2,
        n,
        CAST(2.00 + ((n % 9) * 0.50) AS DECIMAL(5,2)),
        CONCAT(N'category-', n),
        CASE WHEN n % 6 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2020-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    WHERE n BETWEEN 21 AND 100
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Categories OFF;

    SET IDENTITY_INSERT dbo.Brands ON;

    INSERT INTO dbo.Brands
    (
        BrandID,
        CountryID,
        BrandCode,
        BrandName,
        FoundedYear,
        BrandType,
        WebsiteUrl,
        SupportEmail,
        ReputationScore,
        IsPrivateLabel,
        CreatedAt
    )
    SELECT
        n,
        n,
        CONCAT(N'BRD', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Brand ', n),
        1950 + (n % 70),
        CASE n % 3
            WHEN 0 THEN N'Global'
            WHEN 1 THEN N'Regional'
            ELSE N'Specialty'
        END,
        CONCAT(N'https://brand', n, N'.demo.local'),
        CONCAT(N'brand', RIGHT(CONCAT(N'000', n), 3), N'@demo.local'),
        CAST(70.00 + ((n % 25) * 0.90) AS DECIMAL(5,2)),
        CASE WHEN n % 10 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2015-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Brands OFF;

    SET IDENTITY_INSERT dbo.Products ON;

    INSERT INTO dbo.Products
    (
        ProductID,
        CategoryID,
        BrandID,
        SupplierID,
        CreatedByEmployeeID,
        ProductCode,
        ProductName,
        SKURoot,
        ProductType,
        UnitOfMeasure,
        WeightKg,
        ShelfLifeDays,
        ReorderLevel,
        StandardCost,
        Description,
        AttributesJson,
        IsDiscontinued,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 19) % 100) + 1,
        ((n + 9) % 100) + 1,
        ((n + 39) % 100) + 1,
        CONCAT(N'PRD', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Product ', n),
        CONCAT(N'SKU-', RIGHT(CONCAT(N'000000', n), 6)),
        CASE n % 5
            WHEN 0 THEN N'FinishedGood'
            WHEN 1 THEN N'Bundle'
            WHEN 2 THEN N'Consumable'
            WHEN 3 THEN N'Service'
            ELSE N'Accessory'
        END,
        CASE n % 4
            WHEN 0 THEN N'Each'
            WHEN 1 THEN N'Box'
            WHEN 2 THEN N'Pack'
            ELSE N'Case'
        END,
        CAST(0.30 + (n * 0.06) AS DECIMAL(10,2)),
        90 + n,
        10 + (n % 25),
        CAST(20.00 + (n * 1.85) AS DECIMAL(18,2)),
        CONCAT(N'Product description for item ', n),
        CONCAT(N'{"family":"', ((n - 1) % 10) + 1, N'","season":"', CASE WHEN n % 2 = 0 THEN N'spring' ELSE N'fall' END, N'"}'),
        CASE WHEN n % 25 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2022-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Products OFF;

    SET IDENTITY_INSERT dbo.ProductVariants ON;

    INSERT INTO dbo.ProductVariants
    (
        ProductVariantID,
        ProductID,
        VariantCode,
        VariantName,
        ColorName,
        SizeLabel,
        Barcode,
        UnitVolumeCm3,
        UnitWeightKg,
        MSRP,
        IsDefaultVariant,
        IsActive,
        CreatedAt
    )
    SELECT
        n,
        n,
        CONCAT(N'VAR', RIGHT(CONCAT(N'0000', n), 4)),
        CONCAT(N'Variant ', n),
        CASE n % 6
            WHEN 0 THEN N'Red'
            WHEN 1 THEN N'Blue'
            WHEN 2 THEN N'Black'
            WHEN 3 THEN N'White'
            WHEN 4 THEN N'Green'
            ELSE N'Silver'
        END,
        CASE n % 5
            WHEN 0 THEN N'XS'
            WHEN 1 THEN N'S'
            WHEN 2 THEN N'M'
            WHEN 3 THEN N'L'
            ELSE N'XL'
        END,
        CONCAT(N'200000', RIGHT(CONCAT(N'000000', n), 6)),
        CAST(100.00 + (n * 12.50) AS DECIMAL(12,2)),
        CAST(0.25 + (n * 0.05) AS DECIMAL(10,2)),
        CAST(49.00 + (n * 2.50) AS DECIMAL(18,2)),
        1,
        CASE WHEN n % 16 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2022-02-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.ProductVariants OFF;

    SET IDENTITY_INSERT dbo.ProductPrices ON;

    INSERT INTO dbo.ProductPrices
    (
        ProductPriceID,
        ProductVariantID,
        ApprovedByEmployeeID,
        PriceType,
        CurrencyCode,
        PriceAmount,
        CostAmount,
        EffectiveFrom,
        EffectiveTo,
        DiscountPercent,
        TaxIncluded,
        PriceSource,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 4) % 100) + 1,
        CASE n % 3
            WHEN 0 THEN N'Retail'
            WHEN 1 THEN N'Wholesale'
            ELSE N'Promotion'
        END,
        CONCAT(N'C', CHAR(65 + ((n - 1) / 26)), CHAR(65 + ((n - 1) % 26))),
        CAST(55.00 + (n * 2.45) AS DECIMAL(18,2)),
        CAST(25.00 + (n * 1.75) AS DECIMAL(18,2)),
        DATEADD(DAY, n, CAST('2024-01-01' AS DATE)),
        CASE WHEN n % 4 = 0 THEN DATEADD(DAY, 45, DATEADD(DAY, n, CAST('2024-01-01' AS DATE))) ELSE NULL END,
        CAST(CASE WHEN n % 3 = 0 THEN (n % 15) * 1.00 ELSE 0 END AS DECIMAL(5,2)),
        CASE WHEN n % 2 = 0 THEN 1 ELSE 0 END,
        CASE n % 3
            WHEN 0 THEN N'PricingEngine'
            WHEN 1 THEN N'ManualApproval'
            ELSE N'Campaign'
        END,
        DATEADD(DAY, n, CAST('2024-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.ProductPrices OFF;

    SET IDENTITY_INSERT dbo.InventoryBalances ON;

    INSERT INTO dbo.InventoryBalances
    (
        InventoryBalanceID,
        WarehouseID,
        ProductVariantID,
        QuantityOnHand,
        QuantityReserved,
        QuantityInTransit,
        AverageUnitCost,
        LastCountedAt,
        LotNumber,
        ExpirationDate,
        IsBlocked,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 49) % 100) + 1,
        200 + (n * 3),
        n % 20,
        n % 15,
        CAST(24.00 + (n * 1.55) AS DECIMAL(18,2)),
        DATEADD(DAY, n, CAST('2025-01-01' AS DATETIME2(0))),
        CONCAT(N'LOT-', RIGHT(CONCAT(N'00000', n), 5)),
        CASE WHEN n % 4 = 0 THEN DATEADD(DAY, 180 + n, CAST('2026-01-01' AS DATE)) ELSE NULL END,
        CASE WHEN n % 17 = 0 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2025-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.InventoryBalances OFF;

    SET IDENTITY_INSERT dbo.PurchaseOrders ON;

    INSERT INTO dbo.PurchaseOrders
    (
        PurchaseOrderID,
        SupplierID,
        WarehouseID,
        RequestedByEmployeeID,
        ApprovedByEmployeeID,
        PurchaseOrderNumber,
        OrderDate,
        ExpectedDate,
        ReceivedDate,
        OrderStatus,
        SubtotalAmount,
        TaxAmount,
        FreightAmount,
        DiscountAmount,
        TotalAmount,
        PaymentStatus,
        Notes,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 19) % 100) + 1,
        ((n + 9) % 100) + 1,
        ((n + 10) % 100) + 1,
        CONCAT(N'PO2026', RIGHT(CONCAT(N'0000', n), 4)),
        DATEADD(DAY, n, CAST('2025-01-01' AS DATE)),
        DATEADD(DAY, 7 + n, CAST('2025-01-01' AS DATE)),
        CASE WHEN n % 4 IN (0, 3) THEN DATEADD(DAY, 10 + n, CAST('2025-01-01' AS DATE)) ELSE NULL END,
        CASE n % 4
            WHEN 0 THEN N'Closed'
            WHEN 1 THEN N'Draft'
            WHEN 2 THEN N'Approved'
            ELSE N'Received'
        END,
        CAST(500.00 + (n * 50.00) AS DECIMAL(18,2)),
        CAST((500.00 + (n * 50.00)) * 0.08 AS DECIMAL(18,2)),
        CAST(20.00 + (n * 2.00) AS DECIMAL(18,2)),
        CAST((n % 10) * 5.00 AS DECIMAL(18,2)),
        CAST((500.00 + (n * 50.00)) + ((500.00 + (n * 50.00)) * 0.08) + (20.00 + (n * 2.00)) - ((n % 10) * 5.00) AS DECIMAL(18,2)),
        CASE n % 4
            WHEN 0 THEN N'Paid'
            WHEN 1 THEN N'Pending'
            WHEN 2 THEN N'Partial'
            ELSE N'Scheduled'
        END,
        CONCAT(N'Purchase order note ', n),
        DATEADD(DAY, n, CAST('2025-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.PurchaseOrders OFF;

    SET IDENTITY_INSERT dbo.PurchaseOrderLines ON;

    INSERT INTO dbo.PurchaseOrderLines
    (
        PurchaseOrderLineID,
        PurchaseOrderID,
        ProductVariantID,
        OrderedQty,
        ReceivedQty,
        RejectedQty,
        UnitCost,
        DiscountPercent,
        TaxRate,
        LineTotal,
        ExpectedReceiptDate,
        QualityStatus,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 49) % 100) + 1,
        5 + (n % 20),
        CASE WHEN n % 4 = 0 THEN (5 + (n % 20)) - 1 ELSE 5 + (n % 20) END,
        CASE WHEN n % 10 = 0 THEN 1 ELSE 0 END,
        CAST(22.00 + (n * 1.55) AS DECIMAL(18,2)),
        CAST((n % 5) * 1.50 AS DECIMAL(5,2)),
        CAST(8.00 + (n % 3) AS DECIMAL(5,2)),
        CAST(
            ((5 + (n % 20)) * (22.00 + (n * 1.55)))
            * (1 - (((n % 5) * 1.50) / 100.0))
            * (1 + ((8.00 + (n % 3)) / 100.0))
            AS DECIMAL(18,2)
        ),
        DATEADD(DAY, 7 + n, CAST('2025-01-01' AS DATE)),
        CASE n % 4
            WHEN 0 THEN N'Rejected'
            WHEN 1 THEN N'Pending'
            WHEN 2 THEN N'Accepted'
            ELSE N'AcceptedWithNote'
        END,
        DATEADD(DAY, n, CAST('2025-01-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.PurchaseOrderLines OFF;

    SET IDENTITY_INSERT dbo.SalesOrders ON;

    INSERT INTO dbo.SalesOrders
    (
        SalesOrderID,
        CustomerID,
        StoreLocationID,
        SalesRepEmployeeID,
        BillingAddressID,
        ShippingAddressID,
        SalesOrderNumber,
        OrderDate,
        RequiredDate,
        ShippedDate,
        OrderStatus,
        ChannelName,
        SubtotalAmount,
        DiscountAmount,
        TaxAmount,
        ShippingAmount,
        TotalAmount,
        PriorityLevel,
        CustomerNote,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 9) % 100) + 1,
        ((n + 19) % 100) + 1,
        n,
        (n % 100) + 1,
        CONCAT(N'SO2026', RIGHT(CONCAT(N'0000', n), 4)),
        DATEADD(DAY, n, CAST('2025-06-01' AS DATETIME2(0))),
        DATEADD(DAY, 3 + n, CAST('2025-06-01' AS DATE)),
        CASE WHEN n % 5 IN (0, 4) THEN DATEADD(DAY, 2 + n, CAST('2025-06-01' AS DATE)) ELSE NULL END,
        CASE n % 5
            WHEN 0 THEN N'Delivered'
            WHEN 1 THEN N'New'
            WHEN 2 THEN N'Confirmed'
            WHEN 3 THEN N'Packed'
            ELSE N'Shipped'
        END,
        CASE n % 4
            WHEN 0 THEN N'Online'
            WHEN 1 THEN N'Store'
            WHEN 2 THEN N'Phone'
            ELSE N'Marketplace'
        END,
        CAST(150.00 + (n * 25.00) AS DECIMAL(18,2)),
        CAST((n % 10) * 3.50 AS DECIMAL(18,2)),
        CAST((150.00 + (n * 25.00)) * 0.08 AS DECIMAL(18,2)),
        CAST(5.00 + (n * 0.75) AS DECIMAL(18,2)),
        CAST((150.00 + (n * 25.00)) - ((n % 10) * 3.50) + ((150.00 + (n * 25.00)) * 0.08) + (5.00 + (n * 0.75)) AS DECIMAL(18,2)),
        ((n - 1) % 5) + 1,
        CONCAT(N'Customer note for order ', n),
        DATEADD(DAY, n, CAST('2025-06-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.SalesOrders OFF;

    SET IDENTITY_INSERT dbo.SalesOrderLines ON;

    INSERT INTO dbo.SalesOrderLines
    (
        SalesOrderLineID,
        SalesOrderID,
        ProductVariantID,
        QuantityOrdered,
        QuantityShipped,
        QuantityReturned,
        UnitPrice,
        UnitCost,
        DiscountPercent,
        TaxRate,
        LineTotal,
        FulfillmentStatus,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 24) % 100) + 1,
        1 + (n % 5),
        CASE WHEN n % 5 IN (0, 4) THEN 1 + (n % 5) ELSE 0 END,
        CASE WHEN n % 10 = 0 THEN 1 ELSE 0 END,
        CAST(55.00 + (n * 2.45) AS DECIMAL(18,2)),
        CAST(25.00 + (n * 1.75) AS DECIMAL(18,2)),
        CAST((n % 6) * 1.25 AS DECIMAL(5,2)),
        CAST(8.00 + (n % 3) AS DECIMAL(5,2)),
        CAST(
            ((1 + (n % 5)) * (55.00 + (n * 2.45)))
            * (1 - (((n % 6) * 1.25) / 100.0))
            * (1 + ((8.00 + (n % 3)) / 100.0))
            AS DECIMAL(18,2)
        ),
        CASE n % 5
            WHEN 0 THEN N'Returned'
            WHEN 1 THEN N'Allocated'
            WHEN 2 THEN N'Backordered'
            WHEN 3 THEN N'Packed'
            ELSE N'Shipped'
        END,
        DATEADD(DAY, n, CAST('2025-06-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.SalesOrderLines OFF;

    SET IDENTITY_INSERT dbo.Payments ON;

    INSERT INTO dbo.Payments
    (
        PaymentID,
        SalesOrderID,
        CustomerID,
        ProcessedByEmployeeID,
        PaymentReference,
        PaymentDate,
        PaymentMethod,
        PaymentChannel,
        CurrencyCode,
        AmountPaid,
        GatewayFee,
        PaymentStatus,
        AuthorizationCode,
        SettlementDate,
        RefundAmount,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        ((n + 29) % 100) + 1,
        CONCAT(N'PAY2026', RIGHT(CONCAT(N'0000', n), 4)),
        DATEADD(DAY, n, CAST('2025-06-02' AS DATETIME2(0))),
        CASE n % 5
            WHEN 0 THEN N'Voucher'
            WHEN 1 THEN N'Cash'
            WHEN 2 THEN N'CreditCard'
            WHEN 3 THEN N'BankTransfer'
            ELSE N'EWallet'
        END,
        CASE n % 4
            WHEN 0 THEN N'POS'
            WHEN 1 THEN N'Gateway'
            WHEN 2 THEN N'Bank'
            ELSE N'MobileApp'
        END,
        CONCAT(N'C', CHAR(65 + ((n - 1) / 26)), CHAR(65 + ((n - 1) % 26))),
        CAST((150.00 + (n * 25.00)) - ((n % 10) * 3.50) + ((150.00 + (n * 25.00)) * 0.08) + (5.00 + (n * 0.75)) AS DECIMAL(18,2)),
        CAST(CASE WHEN n % 5 IN (2, 4) THEN 2.50 + (n * 0.10) ELSE 0.00 END AS DECIMAL(18,2)),
        CASE n % 4
            WHEN 0 THEN N'Refunded'
            WHEN 1 THEN N'Pending'
            WHEN 2 THEN N'Authorized'
            ELSE N'Captured'
        END,
        CONCAT(N'AUTH', RIGHT(CONCAT(N'000000', n), 6)),
        CASE WHEN n % 4 IN (0, 3) THEN DATEADD(DAY, 2 + n, CAST('2025-06-02' AS DATE)) ELSE NULL END,
        CAST(CASE WHEN n % 4 = 0 THEN 5.00 + n ELSE 0.00 END AS DECIMAL(18,2)),
        DATEADD(DAY, n, CAST('2025-06-02' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Payments OFF;

    SET IDENTITY_INSERT dbo.Shipments ON;

    INSERT INTO dbo.Shipments
    (
        ShipmentID,
        SalesOrderID,
        WarehouseID,
        StoreLocationID,
        CarrierName,
        ServiceLevel,
        TrackingNumber,
        ShippedDate,
        DeliveredDate,
        ShipmentStatus,
        PackageCount,
        TotalWeightKg,
        ShippingCost,
        SignatureRequired,
        RecipientName,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 19) % 100) + 1,
        ((n + 9) % 100) + 1,
        CASE n % 4
            WHEN 0 THEN N'DHL'
            WHEN 1 THEN N'FedEx'
            WHEN 2 THEN N'UPS'
            ELSE N'LocalExpress'
        END,
        CASE n % 3
            WHEN 0 THEN N'Standard'
            WHEN 1 THEN N'Express'
            ELSE N'SameDay'
        END,
        CONCAT(N'TRK', RIGHT(CONCAT(N'00000000', n), 8)),
        DATEADD(DAY, n, CAST('2025-06-03' AS DATETIME2(0))),
        CASE WHEN n % 5 = 0 THEN DATEADD(DAY, 2 + n, CAST('2025-06-03' AS DATETIME2(0))) ELSE NULL END,
        CASE n % 5
            WHEN 0 THEN N'Delivered'
            WHEN 1 THEN N'Pending'
            WHEN 2 THEN N'Picked'
            WHEN 3 THEN N'InTransit'
            ELSE N'Delayed'
        END,
        1 + (n % 4),
        CAST(1.00 + (n * 0.15) AS DECIMAL(10,2)),
        CAST(10.00 + (n * 0.95) AS DECIMAL(18,2)),
        CASE WHEN n % 3 = 0 THEN 1 ELSE 0 END,
        CONCAT(N'Recipient ', n),
        DATEADD(DAY, n, CAST('2025-06-03' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Shipments OFF;

    SET IDENTITY_INSERT dbo.Reviews ON;

    INSERT INTO dbo.Reviews
    (
        ReviewID,
        CustomerID,
        ProductID,
        SalesOrderID,
        Rating,
        ReviewTitle,
        ReviewText,
        ReviewStatus,
        HelpfulVotes,
        UnhelpfulVotes,
        WouldRecommend,
        SubmittedAt,
        CreatedAt
    )
    SELECT
        n,
        n,
        ((n + 9) % 100) + 1,
        n,
        (n % 5) + 1,
        CONCAT(N'Review title ', n),
        CONCAT(N'Review text for product order ', n),
        CASE n % 3
            WHEN 0 THEN N'Pending'
            WHEN 1 THEN N'Published'
            ELSE N'Hidden'
        END,
        n % 40,
        n % 5,
        CASE WHEN n % 5 <> 1 THEN 1 ELSE 0 END,
        DATEADD(DAY, n, CAST('2025-07-01' AS DATETIME2(0))),
        DATEADD(DAY, n, CAST('2025-07-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.Reviews OFF;

    SET IDENTITY_INSERT dbo.SupportTickets ON;

    INSERT INTO dbo.SupportTickets
    (
        SupportTicketID,
        CustomerID,
        SalesOrderID,
        AssignedEmployeeID,
        TicketNumber,
        TicketType,
        Subject,
        Description,
        PriorityName,
        TicketStatus,
        OpenedAt,
        FirstResponseAt,
        ClosedAt,
        SatisfactionScore,
        ResolutionSummary,
        TagsJson,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        ((n + 39) % 100) + 1,
        CONCAT(N'TCK2026', RIGHT(CONCAT(N'0000', n), 4)),
        CASE n % 4
            WHEN 0 THEN N'Delivery'
            WHEN 1 THEN N'Billing'
            WHEN 2 THEN N'Product'
            ELSE N'Return'
        END,
        CONCAT(N'Support subject ', n),
        CONCAT(N'Support ticket description ', n),
        CASE n % 4
            WHEN 0 THEN N'Low'
            WHEN 1 THEN N'Medium'
            WHEN 2 THEN N'High'
            ELSE N'Critical'
        END,
        CASE n % 5
            WHEN 0 THEN N'Closed'
            WHEN 1 THEN N'Open'
            WHEN 2 THEN N'Assigned'
            WHEN 3 THEN N'PendingCustomer'
            ELSE N'Resolved'
        END,
        DATEADD(DAY, n, CAST('2025-07-15' AS DATETIME2(0))),
        DATEADD(HOUR, 4, DATEADD(DAY, n, CAST('2025-07-15' AS DATETIME2(0)))),
        CASE WHEN n % 5 IN (0, 4) THEN DATEADD(DAY, 2 + n, CAST('2025-07-15' AS DATETIME2(0))) ELSE NULL END,
        CASE WHEN n % 5 IN (0, 4) THEN CAST(3 + (n % 3) AS TINYINT) ELSE NULL END,
        CONCAT(N'Resolution summary ', n),
        CONCAT(N'{"channel":"', CASE WHEN n % 2 = 0 THEN N'email' ELSE N'phone' END, N'","queue":"', ((n - 1) % 6) + 1, N'"}'),
        DATEADD(DAY, n, CAST('2025-07-15' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.SupportTickets OFF;

    SET IDENTITY_INSERT dbo.ReturnRequests ON;

    INSERT INTO dbo.ReturnRequests
    (
        ReturnRequestID,
        SalesOrderID,
        SalesOrderLineID,
        CustomerID,
        ApprovedByEmployeeID,
        ReturnNumber,
        RequestDate,
        ReasonCode,
        ReasonDescription,
        QuantityRequested,
        QuantityApproved,
        RefundAmount,
        ReturnStatus,
        ReceivedAt,
        Restockable,
        CreatedAt
    )
    SELECT
        n,
        n,
        n,
        n,
        CASE WHEN n % 5 IN (0, 3) THEN ((n + 49) % 100) + 1 ELSE NULL END,
        CONCAT(N'RTN2026', RIGHT(CONCAT(N'0000', n), 4)),
        DATEADD(DAY, n, CAST('2025-08-01' AS DATETIME2(0))),
        CASE n % 5
            WHEN 0 THEN N'DAMAGED'
            WHEN 1 THEN N'SIZE'
            WHEN 2 THEN N'NOT_AS_DESCRIBED'
            WHEN 3 THEN N'LATE_DELIVERY'
            ELSE N'OTHER'
        END,
        CONCAT(N'Return reason description ', n),
        1,
        CASE WHEN n % 5 IN (0, 3) THEN 1 ELSE 0 END,
        CAST(10.00 + (n * 1.50) AS DECIMAL(18,2)),
        CASE n % 5
            WHEN 0 THEN N'Received'
            WHEN 1 THEN N'Requested'
            WHEN 2 THEN N'UnderReview'
            WHEN 3 THEN N'Approved'
            ELSE N'Rejected'
        END,
        CASE WHEN n % 5 = 0 THEN DATEADD(DAY, 5 + n, CAST('2025-08-01' AS DATETIME2(0))) ELSE NULL END,
        CASE WHEN n % 3 = 0 THEN 0 ELSE 1 END,
        DATEADD(DAY, n, CAST('2025-08-01' AS DATETIME2(0)))
    FROM dbo.__SeedNumbers
    ORDER BY n;

    SET IDENTITY_INSERT dbo.ReturnRequests OFF;

DROP TABLE dbo.__SeedNumbers;
SELECT
    t.name AS TableName,
    SUM(p.rows) AS RowCount
FROM sys.tables AS t
INNER JOIN sys.partitions AS p
    ON p.object_id = t.object_id
   AND p.index_id IN (0, 1)
WHERE SCHEMA_NAME(t.schema_id) = N'dbo'
GROUP BY t.name
ORDER BY t.name;
SELECT N'EnterpriseRetailDemo was created successfully.' AS Message;
