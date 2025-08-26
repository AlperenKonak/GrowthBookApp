using GrowthBook;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;  // Asenkron i�lemler (async/await) i�in

public class ApplicationManager
{
    private GrowthBook.GrowthBook _growthBook;
    private Context _context;
    // SQL Server ba�lant� dizesi
    private string _connectionString = "Data Source=localhost;User ID=sa;Password=YourStrong!Passw0rd;Initial Catalog=growth;TrustServerCertificate=True;";


    private static readonly ILoggerFactory _staticLoggerFactory;  // GrowthBook'un dahili loglar�n� konsola yazd�rma
    static ApplicationManager()
    {
        _staticLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    }
    public ApplicationManager() 
    {
        InitializeDatabase();

        _context = new Context  // GrowthBook'un de�erlendirme yaparken kullanaca�� bilgiler
        {
            Enabled = true, // GrowthBook SDK's�n�n etkin olup olmad���n� belirler (true = etkin).
            
            ApiHost = "http://localhost:3100", // GrowthBook API sunucusunun adresi (feature tan�mlar�n� buradan �eker).
           
            ClientKey = "sdk-uJffKiG3A1fh66S", // GrowthBook projenize �zg� istemci anahtar� (kimlik do�rulama i�in).
           
            Attributes = new JObject // GrowthBook'un feature flag'leri hedeflemek i�in kulland��� kullan�c�/uygulama nitelikleri
            {
                ["id"] = "user-123",  // �rnek kullan�c� ID'si
                ["country"] = "UNKNOWN"  // �rnek �lke bilgisi
            },

            LoggerFactory = _staticLoggerFactory,
            TrackingCallback = (Experiment experiment, ExperimentResult experimentResult) => // deney sonu�lar�n� MS SQL Servere g�ndermeyi sa�lar.
            {
                Console.WriteLine($"[Growthbook Tracking] Deney: {experiment.Key}, Varyasyon: {experimentResult.Value}");

                string assignedVariationForDb;
                // experimentResult.Value'nun tipi boolean ise, 0 veya 1'e d�n��t�r
                if (experimentResult.Value.Type == JTokenType.Boolean)
                {
                    bool boolValue = experimentResult.Value.ToObject<bool>();
                    assignedVariationForDb = boolValue ? "1" : "0";
                }
                else
                {
                    // Boolean de�ilse oldu�u gibi kaydet
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

        //  (SaveUserProfile metodunu constructor'larda �a��rma)
        SaveUserProfile(
           _context.Attributes!["id"]!.ToString(),
           _context.Attributes!["country"]!.ToString()
       );
       
    }

    public ApplicationManager(string userId, string country) : this() // Kullan�c� ID'si ve �lke ile ba�latma.
    {                                                              //  ": this()" ile parametresiz yap�land�r�c�y� �a��r�r.


        _context.Attributes!["id"] = userId;   // GrowthBook ba�lam�ndaki kullan�c� ID'sini g�nceller.
       
        _context.Attributes!["country"] = country;  // GrowthBook ba�lam�ndaki �lke bilgisini g�nceller.
       
        _growthBook = new GrowthBook.GrowthBook(_context);  // G�ncellenmi� ba�lam ile GrowthBook SDK's�n� ba�lat�r.



        SaveUserProfile(userId, country);
       
    }

    // InitializeDatabase metodu (MS SQL Server i�in)
    private void InitializeDatabase()
    {

        using (var connection = new SqlConnection(_connectionString))
        {
            Console.WriteLine(_connectionString);
            Console.WriteLine(connection.ConnectionString);
            connection.ConnectionString = _connectionString;
            connection.Open();

            // Metrics tablosunu olu�turmak i�in SQL komutu. E�er tablo yoksa olu�turur.
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

            // ExperimentAssignments tablosunu olu�turmak i�in SQL komutu. E�er tablo yoksa olu�turur.
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
        Console.WriteLine("Veritaban� tablolar� kontrol edildi/olu�turuldu.");
    }

    public async Task InitializeGrowthBookAsync() // Feature flags ve deney tan�mlar�n� GrowthBook API'sinden asenkron olarak y�kler.
    {
        await _growthBook.LoadFeatures();
        Console.WriteLine("Growthbook �zellikleri y�klendi.");
    }

    public void RunApplicationLogic()  // �zellik bayraklar� ve A/B testleri kullan�larak farkl� davran��lar sim�le edilir.
    {
        Console.WriteLine($"Kullan�c� ID: {_growthBook.Attributes!["id"]}, �lke: {_growthBook.Attributes!["country"]}");

        bool isNewAppRolloutOn = _growthBook.IsOn("new-app-rollout");  // �zellik bayra��n�n a��k olup olmad���n� kontrol eder.
        bool isMyCountryOn = _growthBook.IsOn("my-country");

        bool useNewApp = false;
        string decisionReason = ""; // Karar�n nedenini tutmak i�in

        if (isMyCountryOn)
        {
            useNewApp = true;
            decisionReason = "TR'ye �zel kural e�le�ti.";
        }
        else if (isNewAppRolloutOn)
        {
            useNewApp = true;
            decisionReason = "Genel rollout kural� e�le�ti.";
        }
        else
        {
            useNewApp = false;
            decisionReason = "Hi�bir yeni uygulama kural� e�le�medi.";
        }

        if (useNewApp)
        {
            Console.WriteLine($"Yeni uygulama s�r�m� kullan�l�yor ({decisionReason})");
            TrackMetric("new_app_usage", 1);
            TrackMetric("revenue_new_app", 150.75);
            TrackMetric("new_app_utility_score", 5);
            TrackMetric("ab_test_new_app_view", 1);
            TrackMetric("ab_test_new_app_conversion", 1);
        }
        else
        {
            Console.WriteLine($"Eski uygulama s�r�m� kullan�l�yor ({decisionReason})");
            TrackMetric("old_app_usage", 1);
            TrackMetric("revenue_old_app", 120.50);
            TrackMetric("old_app_utility_score", 3);
            TrackMetric("ab_test_old_app_view", 1);
            TrackMetric("ab_test_old_app_conversion", 0);
        }

        Console.WriteLine("\n--- A/B Testi �rne�i ---");
        Console.WriteLine("A/B Testi sonucu do�rudan al�nam�yor (SDK s�r�m k�s�tlamas�).");
        Console.WriteLine("L�tfen konsol ��kt�s�ndaki '[Growthbook Tracking]' loglar�n� kontrol edin.");
        Console.WriteLine("Deney atamalar� veritaban�na kaydediliyor olmal�.");
    }

    // Metrikleri veritaban�na kaydetme
    private void TrackMetric(string metricName, double value)
    {
        Console.WriteLine($"[Uygulama Metri�i] Metrik: {metricName}, De�er: {value}");
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

    // Deney atamalar�n� veritaban�na kaydetme
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
        Console.WriteLine($"[DB] Kullan�c� profili kaydedildi/g�ncellendi: {userId}, �lke: {country}");
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

        Console.WriteLine("\n--- T�rkiye'den Kullan�c� Sim�lasyonu (TR) ---");
        var trUserAppManager = new ApplicationManager("user-TR-001", "TR");
        await trUserAppManager.InitializeGrowthBookAsync();
        trUserAppManager.RunApplicationLogic();
        trUserAppManager.DisposeGrowthBook();

        Console.WriteLine("\n--- Almanya'dan Kullan�c� Sim�lasyonu (DE) ---");
        var deUserAppManager = new ApplicationManager("user-DE-001", "DE");
        await deUserAppManager.InitializeGrowthBookAsync();
        deUserAppManager.RunApplicationLogic();
        deUserAppManager.DisposeGrowthBook();

        for (int i = 0; i < 500; i++)
        {
            Console.WriteLine($"\n--- Kullan�c� Sim�lasyonu {i + 1} ---");
            // Her sim�lasyon i�in farkl� bir kullan�c� ID'si olu�tur
            string userId = $"user-{123 + i}";  // user-123, user-124, user-125 ...
            string country = (i % 2 == 0) ? "TR" : "US"; // Her iki kullan�c�da bir �lkeyi de�i�tirerek test

            var userAppManager = new ApplicationManager(userId, country); 
            await userAppManager.InitializeGrowthBookAsync();
            userAppManager.RunApplicationLogic();
            userAppManager.DisposeGrowthBook();
            await Task.Delay(100);  // Her sim�lasyon aras�nda 100 milisaniye bekler.
        }
        Console.WriteLine("\n��lem tamamland�. Veriler veritaban�na yaz�ld�. ��kmak i�in bir tu�a bas�n.");
        Console.ReadKey();
    }
}
