using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Storage;
using MiScaleExporter.MAUI;
using MiScaleExporter.MAUI.Resources.Localization;
using MiScaleExporter.MAUI.Utils;
using MiScaleExporter.Models;
using MiScaleExporter.Services;
using System.Globalization;
using System.Threading;
using YetAnotherGarminConnectClient.Dto.Garmin.Fit;

#if ANDROID
using Android.Health.Connect;
using Android.Health.Connect.DataTypes;
using Android.Health.Connect.DataTypes.Units;
#endif

namespace MiScaleExporter.MAUI.ViewModels
{
    public class FormViewModel : BaseViewModel, IFormViewModel
    {
        private readonly IGarminService _garminService;
        private readonly IFileSaver _fileSaver;
        private const double KgToLbsConversion = 2.20462;

        public FormViewModel(IGarminService garminService, IFileSaver fileSaver)
        {
            _garminService = garminService;
            Title = AppSnippets.GarminBodyCompositionForm;
            Date = DateTime.Now;
            Time = DateTime.Now.TimeOfDay;
            _muscleMassAsKg = true;
            _showWeightInKg = true;
            UploadCommand = new Command(OnUpload, ValidateSave);
            GenerateFitFileCommand = new Command(OnGenerateFitFileAsync);
            CancelMFACommand = new Command(OnCancelMFA);

            // Инициализация новой команды для Health Connect
            SendToHealthConnectCommand = new Command(OnSendToHealthConnect);

            this.PropertyChanged +=
                (_, __) => UploadCommand.ChangeCanExecute();

            this._fileSaver = fileSaver;
        }

        public async Task LoadPreferencesAsync()
        {
            this._email = Preferences.Get(PreferencesKeys.GarminUserEmail, string.Empty);
            this._password = await SecureStorage.GetAsync(PreferencesKeys.GarminUserPassword);

            this._accessToken = await SecureStorage.GetAsync(PreferencesKeys.GarminUserAccessToken);
            this._tokenSecret = await SecureStorage.GetAsync(PreferencesKeys.GarminUserTokenSecret);
            this._saveTokens = !string.IsNullOrWhiteSpace(_email) && !string.IsNullOrWhiteSpace(_password);

            this.ShowEmail = string.IsNullOrWhiteSpace(_email);
            this.ShowPassword = string.IsNullOrWhiteSpace(_password);

            this._displayWeightInLbs = Preferences.Get(PreferencesKeys.DisplayWeightInLbs, false);
            this.ShowWeightInKg = !_displayWeightInLbs;
            this.ShowWeightInLbs = _displayWeightInLbs;

            this.MuscleMassAsPercentage = Preferences.Get(PreferencesKeys.MuscleMassAsPercentage, false);
            this.MuscleMassAsKg = (!MuscleMassAsPercentage && ShowWeightInKg);
            this.MuscleMassAsLbs = (!MuscleMassAsPercentage && !ShowWeightInKg);


            this.Date = DateTime.Now;
            this.Time = DateTime.Now.TimeOfDay;
        }
        private bool ValidateSave()
        {
            return !String.IsNullOrWhiteSpace(_email)
                   && !String.IsNullOrWhiteSpace(_password);
        }

        public void AutoUpload()
        {
            if (!string.IsNullOrWhiteSpace(_email)
                 && !string.IsNullOrWhiteSpace(_password))
            {
                OnUpload();
            }
        }

        private async void OnUpload()
        {
            this.IsBusyForm = true;
            var credencials = new CredentialsData
            {
                Email = _email,
                Password = _password,
                AccessToken = this._accessToken,
                TokenSecret = this._tokenSecret,
            };
            var response = await this._garminService.UploadAsync(this.PrepareRequest(), Date.Date.Add(Time), credencials);
            var message = (response?.IsSuccess ?? false) ? AppSnippets.Uploaded : response?.Message;
            await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, message, AppSnippets.OK);
            this.IsBusyForm = false;
            if (this._saveTokens)
            {
                this._accessToken = response?.AccessToken ?? string.Empty;
                this._tokenSecret = response?.TokenSecret ?? string.Empty;
            }
            else
            {
                this._accessToken = string.Empty;
                this._tokenSecret = string.Empty;
            }
            await SecureStorage.SetAsync(PreferencesKeys.GarminUserAccessToken, this._accessToken);
            await SecureStorage.SetAsync(PreferencesKeys.GarminUserTokenSecret, this._tokenSecret);
            if (response?.MFARequested ?? false)
            {
                this.ShowMFACode = true;
                this.ShowEmail = false;
                this.ShowPassword = false;
                this.ExternalApiClientId = response?.ExternalApiClientId;
            }
            else
            {
                this.ShowMFACode = false;
                this.MFACode = null;
                this.ExternalApiClientId = null;
                this.ShowEmail = string.IsNullOrWhiteSpace(Preferences.Get(PreferencesKeys.GarminUserEmail, string.Empty));
                this.ShowPassword = string.IsNullOrWhiteSpace(await SecureStorage.GetAsync(PreferencesKeys.GarminUserPassword));
            }
            // This will pop the current page off the navigation stack
            await Shell.Current.GoToAsync("..?autoUpload=false");
        }

        private async void OnGenerateFitFileAsync()
        {
            this.IsBusyForm = true;

            var response = await this._garminService.GenerateFitFileAsync(this.PrepareRequest(), Date.Date.Add(Time));

            if (!response.IsSuccess && response.file != null)
            {
                await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, response?.Message, AppSnippets.OK);
            }
            else
            {
                using var stream = new MemoryStream(response.file);
                var fileSaverResult = await _fileSaver.SaveAsync($"activity_{Date.Date.Add(Time).ToShortDateString()}.fit", stream);
                if (fileSaverResult.IsSuccessful)
                {
                    await Toast.Make($"The file was saved successfully to location: {fileSaverResult.FilePath}").Show();
                }
                else
                {
                    await Toast.Make($"The file was not saved successfully with error: {fileSaverResult.Exception.Message}").Show();
                }
            }

            this.IsBusyForm = false;

            // This will pop the current page off the navigation stack
            await Shell.Current.GoToAsync("..?autoUpload=false");
        }

        private async void OnCancelMFA()
        {
            this.ShowMFACode = false;
            this.MFACode = null;
            this.ExternalApiClientId = null;
            this.ShowEmail = string.IsNullOrWhiteSpace(Preferences.Get(PreferencesKeys.GarminUserEmail, string.Empty));
            this.ShowPassword = string.IsNullOrWhiteSpace(await SecureStorage.GetAsync(PreferencesKeys.GarminUserPassword));
        }

        private BodyComposition PrepareRequest()
        {
            var bc = new BodyComposition
            {
                Fat = DoubleValueParser.ParseValueFromUsersCulture(_fat) ?? 0,
                BodyType = _bodyType ?? 0,
                Weight = ConvertToKg(DoubleValueParser.ParseValueFromUsersCulture(_weight) ?? 0),
                BoneMass = ConvertToKg(DoubleValueParser.ParseValueFromUsersCulture(_boneMass) ?? 0),
                MuscleMass = ConvertToKg(DoubleValueParser.ParseValueFromUsersCulture(_muscleMass) ?? 0),
                MetabolicAge = DoubleValueParser.ParseValueFromUsersCulture(_metabolicAge) ?? 0,
                ProteinPercentage = DoubleValueParser.ParseValueFromUsersCulture(_proteinPercentage) ?? 0,
                VisceralFat = DoubleValueParser.ParseValueFromUsersCulture(_visceralFat) ?? 0,
                BMI = DoubleValueParser.ParseValueFromUsersCulture(_bmi) ?? 0,
                BMR = DoubleValueParser.ParseValueFromUsersCulture(_bmr) ?? 0,
                WaterPercentage = DoubleValueParser.ParseValueFromUsersCulture(_waterPercentage) ?? 0,
                MFACode = _mfaCode,
                ExternalApiClientId = _externalApiClientId
            };

            if (Preferences.Get(PreferencesKeys.MuscleMassAsPercentage, false)
                && bc.MuscleMass != 0
                && bc.Weight != 0)
            {
                bc.MuscleMass = (bc.MuscleMass / 100) * bc.Weight;
            }
            return bc;
        }

        public void LoadBodyComposition()
        {
            if (App.BodyComposition is null) return;

            Weight = ConvertFromKg(App.BodyComposition.Weight).ToString("0.##");
            BMI = App.BodyComposition.BMI.ToString();
            BoneMass = ConvertFromKg(App.BodyComposition.BoneMass).ToString("0.##");
            MuscleMass = ConvertFromKg(App.BodyComposition.MuscleMass).ToString("0.##");
            IdealWeight = ConvertFromKg(App.BodyComposition.IdealWeight).ToString("0.##");
            BMR = App.BodyComposition.BMR.ToString();
            MetabolicAge = App.BodyComposition.MetabolicAge.ToString();
            ProteinPercentage = App.BodyComposition.ProteinPercentage.ToString();
            VisceralFat = App.BodyComposition.VisceralFat.ToString();
            Fat = App.BodyComposition.Fat.ToString();
            WaterPercentage = App.BodyComposition.WaterPercentage.ToString();
            BodyType = App.BodyComposition.BodyType;
            IsAutomaticCalculation = true;
        }

        // --- Реализация метода отправки данных в Health Connect ---
        private async void OnSendToHealthConnect()
        {
#if ANDROID
            try
            {
                if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.UpsideDownCake)
                {
                    await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.HealthConnectMinVersionError, AppSnippets.OK);
                    return;
                }

                this.IsBusyForm = true;

                var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity ?? Android.App.Application.Context;

                // Список необходимых разрешений
                string[] permissions = new string[]
                {
                    "android.permission.health.WRITE_WEIGHT",
                    "android.permission.health.WRITE_BODY_FAT",
                    "android.permission.health.WRITE_BONE_MASS",
                    "android.permission.health.WRITE_BODY_WATER_MASS"
                };

                // Проверяем, выданы ли разрешения пользователем
                bool allGranted = true;
                foreach (var perm in permissions)
                {
                    if (context.CheckSelfPermission(perm) != Android.Content.PM.Permission.Granted)
                    {
                        allGranted = false;
                        break;
                    }
                }

                // Если хотя бы одно разрешение не выдано, открываем системные настройки Health Connect для нашего приложения
                if (!allGranted)
                {
                    await Application.Current.MainPage.DisplayAlert(
                        AppSnippets.HealthConnectPermissionsTitle,
                        AppSnippets.HealthConnectPermissionsMessage,
                        AppSnippets.OK);

                    Android.Content.Intent intent;
                    if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.UpsideDownCake)
                    {
                        // Полностью открытый системный интент для Android 14+, который не вызывает ошибку SecurityException
                        intent = new Android.Content.Intent("android.health.connect.action.HEALTH_HOME_SETTINGS");
                    }
                    else
                    {
                        // Интент для Android 13 и младше
                        intent = new Android.Content.Intent("androidx.health.ACTION_HEALTH_CONNECT_SETTINGS");
                    }

                    intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                    context.StartActivity(intent);

                    this.IsBusyForm = false;
                    return;
                }

                var healthConnectManager = (HealthConnectManager)context.GetSystemService("healthconnect");
                if (healthConnectManager == null)
                {
                    await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.HealthConnectError, AppSnippets.OK);
                    this.IsBusyForm = false;
                    return;
                }

                // Извлекаем и нормализуем данные
                var bc = this.PrepareRequest();

                if (bc.Weight <= 0)
                {
                    await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.WeightRequiredError, AppSnippets.OK);
                    this.IsBusyForm = false;
                    return;
                }

                var records = new System.Collections.Generic.List<Record>();
                var now = Java.Time.Instant.Now();
                var metadata = new Metadata.Builder().Build();

                // 1. Вес (WeightRecord) - передается в граммах (кг * 1000)
                var weightMass = Mass.FromGrams(bc.Weight * 1000);
                var weightRecord = new WeightRecord.Builder(metadata, now, weightMass).Build();
                records.Add(weightRecord);

                // 2. Процент жира (BodyFatRecord) - передается в процентах от 0 до 100
                if (bc.Fat > 0)
                {
                    var fatPercent = Percentage.FromValue(bc.Fat);
                    var bodyFatRecord = new BodyFatRecord.Builder(metadata, now, fatPercent).Build();
                    records.Add(bodyFatRecord);
                }

                // 3. Костная масса (BoneMassRecord) - передается в граммах (кг * 1000)
                if (bc.BoneMass > 0)
                {
                    var boneMass = Mass.FromGrams(bc.BoneMass * 1000);
                    var boneRecord = new BoneMassRecord.Builder(metadata, now, boneMass).Build();
                    records.Add(boneRecord);
                }

                // 4. Масса воды (BodyWaterMassRecord) 
                // Высчитываем массу воды: (Процент воды / 100) * Общий вес в кг
                if (bc.WaterPercentage > 0)
                {
                    double waterInKg = (bc.WaterPercentage / 100.0) * bc.Weight;
                    var waterMass = Mass.FromGrams(waterInKg * 1000);
                    var waterRecord = new BodyWaterMassRecord.Builder(metadata, now, waterMass).Build();
                    records.Add(waterRecord);
                }

                // Отправка данных
                var executor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor();
                var tcs = new TaskCompletionSource<bool>();
                var callback = new HealthConnectCallback(tcs);

                healthConnectManager.InsertRecords(records, executor, callback);

                bool success = await tcs.Task;

                if (success)
                {
                    await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.HealthConnectSuccess, AppSnippets.OK);
                }
                else
                {
                    await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.HealthConnectError, AppSnippets.OK);
                }
            }
            catch (Exception ex)
            {
                await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, $"{AppSnippets.HealthConnectError}: {ex.Message}", AppSnippets.OK);
            }
            finally
            {
                this.IsBusyForm = false;
            }
#else
            await Application.Current.MainPage.DisplayAlert(AppSnippets.Response, AppSnippets.HealthConnectNotSupported, AppSnippets.OK);
#endif
        }

        public Command UploadCommand { get; }
        public Command CancelMFACommand { get; }

        public Command GenerateFitFileCommand { get; }

        // Свойство для привязки кнопки к Health Connect
        public Command SendToHealthConnectCommand { get; }

        private string _weight;

        public string Weight
        {
            get => _weight;
            set => SetProperty(ref _weight, DoubleValueParser.CheckValue(value));
        }

        private string _bmi;

        public string BMI
        {
            get => _bmi;
            set => SetProperty(ref _bmi, DoubleValueParser.CheckValue(value));
        }

        private string _idealWeight;

        public string IdealWeight
        {
            get => _idealWeight;
            set => SetProperty(ref _idealWeight, DoubleValueParser.CheckValue(value));
        }

        private string _metabolicAge;

        public string MetabolicAge
        {
            get => _metabolicAge;
            set => SetProperty(ref _metabolicAge, DoubleValueParser.CheckValue(value));
        }

        private string _proteinPercentage;

        public string ProteinPercentage
        {
            get => _proteinPercentage;
            set => SetProperty(ref _proteinPercentage, DoubleValueParser.CheckValue(value));
        }

        private string _bmr;

        public string BMR
        {
            get => _bmr;
            set => SetProperty(ref _bmr, DoubleValueParser.CheckValue(value));
        }

        private string _fat;

        public string Fat
        {
            get => _fat;
            set => SetProperty(ref _fat, DoubleValueParser.CheckValue(value));
        }

        private string _muscleMass;

        public string MuscleMass
        {
            get => _muscleMass;
            set => SetProperty(ref _muscleMass, DoubleValueParser.CheckValue(value));
        }

        private string _boneMass;

        public string BoneMass
        {
            get => _boneMass;
            set => SetProperty(ref _boneMass, DoubleValueParser.CheckValue(value));
        }

        private string _visceralFat;

        public string VisceralFat
        {
            get => _visceralFat;
            set => SetProperty(ref _visceralFat, DoubleValueParser.CheckValue(value));
        }

        private int? _bodyType;

        public int? BodyType
        {
            get => _bodyType;
            set => SetProperty(ref _bodyType, value);
        }

        private string _waterPercentage;

        public string WaterPercentage
        {
            get => _waterPercentage;
            set => SetProperty(ref _waterPercentage, DoubleValueParser.CheckValue(value));
        }

        private string _email;

        private string _password;

        private string _accessToken;

        private string _tokenSecret;
        private bool _saveTokens;

        private DateTime _date;

        public DateTime Date
        {
            get => _date;
            set => SetProperty(ref _date, value);
        }

        private TimeSpan _time;

        public TimeSpan Time
        {
            get => _time;
            set => SetProperty(ref _time, value);
        }

        private bool _isAutomaticCalculation;

        public bool IsAutomaticCalculation
        {
            get => _isAutomaticCalculation;
            set => SetProperty(ref _isAutomaticCalculation, value);
        }

        private bool _isBusyForm;

        public bool IsBusyForm
        {
            get => _isBusyForm;
            set => SetProperty(ref _isBusyForm, value);
        }

        private bool _showMFACode;
        public bool ShowMFACode
        {
            get => _showMFACode;
            set => SetProperty(ref _showMFACode, value);
        }

        private string _externalApiClientId;
        public string ExternalApiClientId
        {
            get => _externalApiClientId;
            set
            {
                SetProperty(ref _externalApiClientId, value);
            }
        }
        private string _mfaCode;
        public string MFACode
        {
            get => _mfaCode;
            set
            {
                SetProperty(ref _mfaCode, value);
            }
        }

        private bool _muscleMassAsPercentage;
        public bool MuscleMassAsPercentage
        {
            get => _muscleMassAsPercentage;
            set => SetProperty(ref _muscleMassAsPercentage, value);
        }

        private bool _muscleMassAsKg;
        public bool MuscleMassAsKg
        {
            get => _muscleMassAsKg;
            set => SetProperty(ref _muscleMassAsKg, value);
        }

        private bool _muscleMassAsLbs;
        public bool MuscleMassAsLbs
        {
            get => _muscleMassAsLbs;
            set => SetProperty(ref _muscleMassAsLbs, value);
        }

        private bool _showWeightInKg;
        public bool ShowWeightInKg
        {
            get => _showWeightInKg;
            set => SetProperty(ref _showWeightInKg, value);
        }
        private bool _showWeightInLbs;
        public bool ShowWeightInLbs
        {
            get => _showWeightInLbs;
            set => SetProperty(ref _showWeightInLbs, value);
        }

        private double ConvertFromKg(double valueInKg)
        {
            return _displayWeightInLbs ? valueInKg * KgToLbsConversion : valueInKg;
        }

        private double ConvertToKg(double displayValue)
        {
            return _displayWeightInLbs ? displayValue / KgToLbsConversion : displayValue;
        }

        private bool _displayWeightInLbs;

        public string Email
        {
            get => _email;
            set
            {
                SetProperty(ref _email, value);
                UploadCommand?.ChangeCanExecute();
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                SetProperty(ref _password, value);
                UploadCommand?.ChangeCanExecute();
            }
        }

        private bool _showEmail;
        private bool _showPassword;

        public bool ShowEmail
        {
            get => _showEmail;
            set => SetProperty(ref _showEmail, value);
        }

        public bool ShowPassword
        {
            get => _showPassword;
            set => SetProperty(ref _showPassword, value);
        }

    }

#if ANDROID
    // Вспомогательный класс для обработки результатов Health Connect
    public class HealthConnectCallback : Java.Lang.Object, Android.OS.IOutcomeReceiver
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public HealthConnectCallback(TaskCompletionSource<bool> tcs)
        {
            _tcs = tcs;
        }

        public void OnResult(Java.Lang.Object result)
        {
            _tcs.TrySetResult(true);
        }

        public void OnError(Java.Lang.Throwable error)
        {
            _tcs.TrySetException(new System.Exception(error.Message));
        }
    }
#endif
}