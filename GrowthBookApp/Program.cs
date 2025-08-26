using GrowthBook;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;  // Asenkron iþlemler (async/await) için

public class ApplicationManager
{
    private GrowthBook.GrowthBook _growthBook;
    private Context _context;
    // SQL Server baðlantý dizesi
    private string _connectionString = "Data Source=localhost;User ID=sa;Password=YourStrong!Passw0rd;Initial Catalog=growth;TrustServerCertificate=True;";


    private static readonly ILoggerFactory _staticLoggerFactory;  // GrowthBook'un dahili loglarýný konsola yazdýrma
    static ApplicationManager()
    {
        _staticLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    }
    public ApplicationManager() 
    {
        InitializeDatabase();

        _context = new Context  // GrowthBook'un deðerlendirme yaparken kullanacaðý bilgiler
        {
            Enabled = true, // GrowthBook SDK'sýnýn etkin olup olmadýðýný belirler (true = etkin).
            
            ApiHost = "http://localhost:3100", // GrowthBook API sunucusunun adresi (feature tanýmlarýný buradan çeker).
           
            ClientKey = "sdk-uJffKiG3A1fh66S", // GrowthBook projenize özgü istemci anahtarý (kimlik doðrulama için).
           
            Attributes = new JObject // GrowthBook'un feature flag'leri hedeflemek için kullandýðý kullanýcý/uygulama nitelikleri
            {
                ["id"] = "user-123",  // Örnek kullanýcý ID'si
                ["country"] = "UNKNOWN"  // Örnek ülke bilgisi
            },

            LoggerFactory = _staticLoggerFactory,
            TrackingCallback = (Experiment experiment, ExperimentResult experimentResult) => // deney sonuçlarýný MS SQL Servere göndermeyi saðlar.
            {
                Console.WriteLine($"[Growthbook Tracking] Deney: {experiment.Key}, Varyasyon: {experimentResult.Value}");

                string assignedVariationForDb;
                // experimentResult.Value'nun tipi boolean ise, 0 veya 1'e dönüþtür
                if (experimentResult.Value.Type == JTokenType.Boolean)
                {
                    bool boolValue = experimentResult.Value.ToObject<bool>();
                    assignedVariationForDb = boolValue ? "1" : "0";
                }
                else
                {
                    // Boolean deðilse olduðu gibi kaydet
                    assignedVariationForDb = experimentResult.Value.ToString();
                }

                SaveExperimentAssignment(
                    _context.Attributes!["id"]!.ToString(),
                    experiment.Key,
                    assignedVariationForDb, 
                    experimentResult.InExperiment
                );
            },

        };

        _growthBook = new GrowthBook.GrowthBook(_context);

        //  (SaveUserProfile metodunu constructor'larda çaðýrma)
        SaveUserProfile(
           _context.Attributes!["id"]!.ToString(),
           _context.Attributes!["country"]!.ToString()
       );
       
    }

    public ApplicationManager(string userId, string country) : this() // Kullanýcý ID'si ve ülke ile baþlatma.
    {                                                              //  ": this()" ile parametresiz yapýlandýrýcýyý çaðýrýr.


        _context.Attributes!["id"] = userId;   // GrowthBook baðlamýndaki kullanýcý ID'sini günceller.
       
        _context.Attributes!["country"] = country;  // GrowthBook baðlamýndaki ülke bilgisini günceller.
       
        _growthBook = new GrowthBook.GrowthBook(_context);  // Güncellenmiþ baðlam ile GrowthBook SDK'sýný baþlatýr.



        SaveUserProfile(userId, country);
       
    }

    // InitializeDatabase metodu (MS SQL Server için)
    private void InitializeDatabase()
    {

        using (var connection = new SqlConnection(_connectionString))
        {
            Console.WriteLine(_connectionString);
            Console.WriteLine(connection.ConnectionString);
            connection.ConnectionString = _connectionString;
            connection.Open();

            // Metrics tablosunu oluþturmak için SQL komutu. Eðer tablo yoksa oluþturur.
            var createMetricsTableCmd = connection.CreateCommand();
            createMetricsTableCmd.CommandText = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Metrics' and xtype='U')
                CREATE TABLE Metrics (
                    Id INT PRIMARY KEY IDENTITY(1,1),
                    UserId NVARCHAR(255) NOT NULL,
                    MetricName NVARCHAR(255) NOT NULL,
                    Value FLOAT NOT NULL,
                    Timestamp DATETIME NOT NULL
                );";
            createMetricsTableCmd.ExecuteNonQuery();

            // ExperimentAssignments tablosunu oluþturmak için SQL komutu. Eðer tablo yoksa oluþturur.
            var createAssignmentsTableCmd = connection.CreateCommand();
            createAssignmentsTableCmd.CommandText = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ExperimentAssignments' and xtype='U')
                CREATE TABLE ExperimentAssignments (
                    Id INT PRIMARY KEY IDENTITY(1,1),
                    UserId NVARCHAR(255) NOT NULL,
                    ExperimentKey NVARCHAR(255) NOT NULL,
                    AssignedVariation NVARCHAR(255) NOT NULL,
                    InExperiment BIT NOT NULL,
                    Timestamp DATETIME NOT NULL
                );";
            createAssignmentsTableCmd.ExecuteNonQuery();

           //  ( USERS TABLOSU )
            var createUsersTableCmd = connection.CreateCommand();
            createUsersTableCmd.CommandText = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Users' and xtype='U')
                CREATE TABLE Users (
                    Id INT PRIMARY KEY IDENTITY(1,1),
                    UserId NVARCHAR(255) NOT NULL UNIQUE,
                    Country NVARCHAR(10) NOT NULL,
                    LastUpdated DATETIME NOT NULL
                );";
            createUsersTableCmd.ExecuteNonQuery();

        }
        Console.WriteLine("Veritabaný tablolarý kontrol edildi/oluþturuldu.");
    }

    public async Task InitializeGrowthBookAsync() // Feature flags ve deney tanýmlarýný GrowthBook API'sinden asenkron olarak yükler.
    {
        await _growthBook.LoadFeatures();
        Console.WriteLine("Growthbook özellikleri yüklendi.");
    }

    public void RunApplicationLogic()  // Özellik bayraklarý ve A/B testleri kullanýlarak farklý davranýþlar simüle edilir.
    {
        Console.WriteLine($"Kullanýcý ID: {_growthBook.Attributes!["id"]}, Ülke: {_growthBook.Attributes!["country"]}");

        bool isNewAppRolloutOn = _growthBook.IsOn("new-app-rollout");  // Özellik bayraðýnýn açýk olup olmadýðýný kontrol eder.
        bool isMyCountryOn = _growthBook.IsOn("my-country");

        bool useNewApp = false;
        string decisionReason = ""; // Kararýn nedenini tutmak için

        if (isMyCountryOn)
        {
            useNewApp = true;
            decisionReason = "TR'ye özel kural eþleþti.";
        }
        else if (isNewAppRolloutOn)
        {
            useNewApp = true;
            decisionReason = "Genel rollout kuralý eþleþti.";
        }
        else
        {
            useNewApp = false;
            decisionReason = "Hiçbir yeni uygulama kuralý eþleþmedi.";
        }

        if (useNewApp)
        {
            Console.WriteLine($"Yeni uygulama sürümü kullanýlýyor ({decisionReason})");
            TrackMetric("new_app_usage", 1);
            TrackMetric("revenue_new_app", 150.75);
            TrackMetric("new_app_utility_score", 5);
            TrackMetric("ab_test_new_app_view", 1);
            TrackMetric("ab_test_new_app_conversion", 1);
        }
        else
        {
            Console.WriteLine($"Eski uygulama sürümü kullanýlýyor ({decisionReason})");
            TrackMetric("old_app_usage", 1);
            TrackMetric("revenue_old_app", 120.50);
            TrackMetric("old_app_utility_score", 3);
            TrackMetric("ab_test_old_app_view", 1);
            TrackMetric("ab_test_old_app_conversion", 0);
        }

        Console.WriteLine("\n--- A/B Testi Örneði ---");
        Console.WriteLine("A/B Testi sonucu doðrudan alýnamýyor (SDK sürüm kýsýtlamasý).");
        Console.WriteLine("Lütfen konsol çýktýsýndaki '[Growthbook Tracking]' loglarýný kontrol edin.");
        Console.WriteLine("Deney atamalarý veritabanýna kaydediliyor olmalý.");
    }

    // Metrikleri veritabanýna kaydetme
    private void TrackMetric(string metricName, double value)
    {
        Console.WriteLine($"[Uygulama Metriði] Metrik: {metricName}, Deðer: {value}");
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
            @"
                INSERT INTO Metrics (UserId, MetricName, Value, Timestamp)
                VALUES (@UserId, @MetricName, @Value, @Timestamp);
            ";
            command.Parameters.AddWithValue("@UserId", _growthBook.Attributes!["id"]!.ToString());
            command.Parameters.AddWithValue("@MetricName", metricName);
            command.Parameters.AddWithValue("@Value", value);
            command.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }
    }

    // Deney atamalarýný veritabanýna kaydetme
    private void SaveExperimentAssignment(string userId, string experimentKey, string assignedVariation, bool inExperiment)
    {
        
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
            @"
                INSERT INTO ExperimentAssignments (UserId, ExperimentKey, AssignedVariation, InExperiment, Timestamp)
                VALUES (@UserId, @ExperimentKey, @AssignedVariation, @InExperiment, @Timestamp);
            ";
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@ExperimentKey", experimentKey);
            command.Parameters.AddWithValue("@AssignedVariation", assignedVariation);
            command.Parameters.AddWithValue("@InExperiment", inExperiment);
            command.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }
    }
    // ( SaveUserProfile metodu )
    private void SaveUserProfile(string userId, string country)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText =
            @"
                MERGE INTO Users AS Target
                USING (VALUES (@UserId, @Country, @Timestamp)) AS Source (UserId, Country, Timestamp)
                ON Target.UserId = Source.UserId
                WHEN MATCHED THEN
                    UPDATE SET Target.Country = Source.Country, Target.LastUpdated = Source.Timestamp
                WHEN NOT MATCHED BY TARGET THEN
                    INSERT (UserId, Country, LastUpdated) VALUES (Source.UserId, Source.Country, Source.Timestamp);
            ";
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Country", country);
            command.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }
        Console.WriteLine($"[DB] Kullanýcý profili kaydedildi/güncellendi: {userId}, Ülke: {country}");
    }

    public void DisposeGrowthBook()
    {
        _growthBook.Dispose();
    }
}

public class Program
{
    public static async Task Main(string[] args)
    {
        var initialAppManager = new ApplicationManager();
        await initialAppManager.InitializeGrowthBookAsync();
        initialAppManager.RunApplicationLogic();
        initialAppManager.DisposeGrowthBook();

        Console.WriteLine("\n--- Türkiye'den Kullanýcý Simülasyonu (TR) ---");
        var trUserAppManager = new ApplicationManager("user-TR-001", "TR");
        await trUserAppManager.InitializeGrowthBookAsync();
        trUserAppManager.RunApplicationLogic();
        trUserAppManager.DisposeGrowthBook();

        Console.WriteLine("\n--- Almanya'dan Kullanýcý Simülasyonu (DE) ---");
        var deUserAppManager = new ApplicationManager("user-DE-001", "DE");
        await deUserAppManager.InitializeGrowthBookAsync();
        deUserAppManager.RunApplicationLogic();
        deUserAppManager.DisposeGrowthBook();

        for (int i = 0; i < 500; i++)
        {
            Console.WriteLine($"\n--- Kullanýcý Simülasyonu {i + 1} ---");
            // Her simülasyon için farklý bir kullanýcý ID'si oluþtur
            string userId = $"user-{123 + i}";  // user-123, user-124, user-125 ...
            string country = (i % 2 == 0) ? "TR" : "US"; // Her iki kullanýcýda bir ülkeyi deðiþtirerek test

            var userAppManager = new ApplicationManager(userId, country); 
            await userAppManager.InitializeGrowthBookAsync();
            userAppManager.RunApplicationLogic();
            userAppManager.DisposeGrowthBook();
            await Task.Delay(100);  // Her simülasyon arasýnda 100 milisaniye bekler.
        }
        Console.WriteLine("\nÝþlem tamamlandý. Veriler veritabanýna yazýldý. Çýkmak için bir tuþa basýn.");
        Console.ReadKey();
    }
}
