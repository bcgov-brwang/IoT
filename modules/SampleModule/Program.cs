// Copyright (c) Microsoft. All rights reserved.
namespace SampleModule
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Newtonsoft.Json;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using Microsoft.Extensions.Logging;
    using System.Diagnostics;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Collections.ObjectModel;
    using System.Linq;
    using MySql.Data.MySqlClient;




    class Program
    {
        const int RetryCount = 5;
        const string MessageCountConfigKey = "MessageCount";
        static readonly ITransientErrorDetectionStrategy TimeoutErrorDetectionStrategy = new DelegateErrorDetectionStrategy(ex => ex.HasTimeoutException());

        static readonly RetryStrategy TransientRetryStrategy =
            new ExponentialBackoff(RetryCount, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(4));

        static readonly Random Rnd = new Random();
        static readonly AtomicBoolean Reset = new AtomicBoolean(false);

        public enum ControlCommandEnum
        {
            Reset = 0,
            Noop = 1
        }

        static List<WeatherData1> jsonList;

        public static int Main() => MainAsync().Result;

        static int count = 1;

        static async Task<int> MainAsync()
        {
            var tns = GetTableNames();
            var stationData = PullData(tns);
            jsonList = ConverToJson(stationData, tns);


            // Console.WriteLine($"[{DateTime.UtcNow.ToString("MM/dd/yyyy hh:mm:ss.fff tt", CultureInfo.InvariantCulture)}] Main()");

            //IConfiguration configuration = new ConfigurationBuilder()
            //    .SetBasePath(Directory.GetCurrentDirectory())
            //    .AddJsonFile("config/appsettings.json", optional: true)
            //    .AddEnvironmentVariables()
            //    .Build();

            //TimeSpan messageDelay = configuration.GetValue("MessageDelay", TimeSpan.FromSeconds(5));
            int messageCount = 1;//configuration.GetValue(MessageCountConfigKey, 500);
            bool sendForever = messageCount < 0;
            //int timeSpane = 3600000;
            //var sim = new SimulatorParameters
            //{
            //    //MachineTempMin = configuration.GetValue<double>("machineTempMin", 21),
            //    //MachineTempMax = configuration.GetValue<double>("machineTempMax", 100),
            //    //MachinePressureMin = configuration.GetValue<double>("machinePressureMin", 1),
            //    //MachinePressureMax = configuration.GetValue<double>("machinePressureMax", 10),
            //    //AmbientTemp = configuration.GetValue<double>("ambientTemp", 21),
            //    //HumidityPercent = configuration.GetValue("ambientHumidity", 25)
            //};




            string messagesToSendString = sendForever ? "unlimited" : messageCount.ToString();
            // Console.WriteLine(
            //     $"Initializing simulated get mysql data to send {messagesToSendString} messages, at an interval of {new TimeSpan(timeSpane).TotalSeconds} seconds.\n"
            //     + $"To change this, set the environment variable {MessageCountConfigKey} to the number of messages that should be sent (set it to -1 to send unlimited messages).");

            Microsoft.Azure.Devices.Client.TransportType transportType = Microsoft.Azure.Devices.Client.TransportType.Amqp_Tcp_Only;
            // Console.WriteLine($"Using transport {transportType.ToString()}");

            var retryPolicy = new RetryPolicy(TimeoutErrorDetectionStrategy, TransientRetryStrategy);
            retryPolicy.Retrying += (_, args) =>
            {
                Console.WriteLine($"Creating ModuleClient failed with exception {args.LastException}");
                if (args.CurrentRetryCount < RetryCount)
                {
                    Console.WriteLine("Retrying...");
                }
            };
            ModuleClient moduleClient = await retryPolicy.ExecuteAsync(() => InitModuleClient(transportType));

            ModuleClient userContext = moduleClient;
            await moduleClient.SetInputMessageHandlerAsync("control", ControlMessageHandle, userContext);

            (CancellationTokenSource cts, ManualResetEventSlim completed, Option<object> handler)
                = ShutdownHandler.Init(TimeSpan.FromSeconds(5), null);

            while (true)
            {

                var tableNames = GetTableNames();

                stationData = PullData(tableNames);
                jsonList = ConverToJson(stationData, tableNames);
                await SendEvents(moduleClient, new TimeSpan(0, 0, 5), sendForever, jsonList, cts, count);
                //await cts.Token.WhenCanceled();
                //completed.Set();
                //handler.ForEach(h => GC.KeepAlive(h));
                Thread.Sleep(60000 * 60);//send message hourly
                Console.WriteLine($"Sending message group {count} again...");
                count++;
            }

            // foreach (var ah in jsonList)
            // {
            //     await SendEvents(moduleClient, new TimeSpan(0, 0, 60), sendForever, messageCount, ah, cts, count);
            //     await cts.Token.WhenCanceled();
            //     completed.Set();
            //     handler.ForEach(h => GC.KeepAlive(h));
            // }

            // return 0;
        }

        static async Task<ModuleClient> InitModuleClient(Microsoft.Azure.Devices.Client.TransportType transportType)
        {
            ITransportSettings[] GetTransportSettings()
            {
                switch (transportType)
                {
                    case Microsoft.Azure.Devices.Client.TransportType.Mqtt:
                    case Microsoft.Azure.Devices.Client.TransportType.Mqtt_Tcp_Only:
                    case Microsoft.Azure.Devices.Client.TransportType.Mqtt_WebSocket_Only:
                        return new ITransportSettings[] { new MqttTransportSettings(transportType) };
                    default:
                        return new ITransportSettings[] { new AmqpTransportSettings(transportType) };
                }
            }

            ITransportSettings[] settings = GetTransportSettings();

            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await moduleClient.OpenAsync().ConfigureAwait(false);
            await moduleClient.SetMethodHandlerAsync("reset", ResetMethod, null);

            // Console.WriteLine("Successfully initialized module client.");
            return moduleClient;
        }

        // Control Message expected to be:
        // {
        //     "command" : "reset"
        // }
        static Task<MessageResponse> ControlMessageHandle(Message message, object userContext)
        {
            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);

            Console.WriteLine($"Received message Body: [{messageString}]");

            try
            {
                var messages = JsonConvert.DeserializeObject<ControlCommand[]>(messageString);

                foreach (ControlCommand messageBody in messages)
                {
                    if (messageBody.Command == ControlCommandEnum.Reset)
                    {
                        Console.WriteLine("Resetting temperature sensor..");
                        Reset.Set(true);
                    }
                    else
                    {
                        // NoOp
                    }
                }
            }
            catch (JsonSerializationException)
            {
                var messageBody = JsonConvert.DeserializeObject<ControlCommand>(messageString);

                if (messageBody.Command == ControlCommandEnum.Reset)
                {
                    Console.WriteLine("Resetting temperature sensor..");
                    Reset.Set(true);
                }
                else
                {
                    // NoOp
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to deserialize control command with exception: [{ex.Message}]");
            }

            return Task.FromResult(MessageResponse.Completed);
        }

        static Task<MethodResponse> ResetMethod(MethodRequest methodRequest, object userContext)
        {
            Console.WriteLine("Received direct method call to reset temperature sensor...");
            Reset.Set(true);
            var response = new MethodResponse((int)HttpStatusCode.OK);
            return Task.FromResult(response);
        }



        /// <summary>
        /// Module behavior:
        ///        Sends data periodically (with default frequency of 5 seconds).
        ///        Data trend:
        ///         - Machine Temperature regularly rises from 21C to 100C in regularly with jitter
        ///         - Machine Pressure correlates with Temperature 1 to 10psi
        ///         - Ambient temperature stable around 21C
        ///         - Humidity is stable with tiny jitter around 25%
        ///                Method for resetting the data stream
        /// </summary>
        static async Task SendEvents(
            ModuleClient moduleClient,
            TimeSpan messageDelay,
            bool sendForever,
            List<WeatherData1> sim,
            CancellationTokenSource cts,
            int count)
        {

            int myCount = 1;
            foreach (var stationData in sim)
            {
                string dataBuffer = JsonConvert.SerializeObject(stationData);
                var eventMessage = new Message(Encoding.UTF8.GetBytes(dataBuffer));
                Console.WriteLine($"\t{DateTime.Now.ToLocalTime()}> Sending message: {myCount}, Body: [{dataBuffer}]");
                await moduleClient.SendEventAsync("temperatureOutput", eventMessage);
                //await Task.Delay(messageDelay, cts.Token);
                myCount++;

            }
            Console.WriteLine($"Done sending {count} messages");

        }

        static void CancelProgram(CancellationTokenSource cts)
        {
            Console.WriteLine("Termination requested, closing.");
            cts.Cancel();
        }

        internal class ControlCommand
        {
            [JsonProperty("command")]
            public ControlCommandEnum Command { get; set; }
        }

        internal class SimulatorParameters
        {
            public double MachineTempMin { get; set; }

            public double MachineTempMax { get; set; }

            public double MachinePressureMin { get; set; }

            public double MachinePressureMax { get; set; }

            public double AmbientTemp { get; set; }

            public int HumidityPercent { get; set; }
        }



        //bruce test 1
        static List<StationDatas> GetAirHumidityList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;
            float maxAirTemp1;
            float curAirTemp1;
            float minAirTemp1;
            float airTempQ;
            float airTemp2;
            float airTemp2Q;
            float rh;
            float dewPoint;
            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);
                    maxAirTemp1 = rdr.GetFloat(4);
                    curAirTemp1 = rdr.GetFloat(5);
                    minAirTemp1 = rdr.GetFloat(6);
                    airTempQ = rdr.GetFloat(7);
                    airTemp2 = rdr.GetFloat(8);
                    airTemp2Q = rdr.GetFloat(9);
                    rh = rdr.GetFloat(10);
                    dewPoint = rdr.GetFloat(11);

                    var airHumid = new AirHumidities();
                    airHumid.deviceId = rdr.GetInt32(2).ToString();
                    airHumid.measurements = new AirHumidityMeasurements();

                    airHumid.measurements.TmStamp = new List<string>();
                    airHumid.measurements.TmStamp.Add(rdr.GetString(0));

                    airHumid.measurements.RecNum = new List<string>();
                    airHumid.measurements.RecNum.Add(rdr.GetString(1));



                    airHumid.measurements.Identifier = new List<float>();
                    airHumid.measurements.Identifier.Add(rdr.GetFloat(3));

                    airHumid.measurements.MaxAirTemp1 = new List<float>();
                    airHumid.measurements.MaxAirTemp1.Add(rdr.GetFloat(4));

                    airHumid.measurements.CurAirTemp1 = new List<float>();
                    airHumid.measurements.CurAirTemp1.Add(rdr.GetFloat(5));

                    airHumid.measurements.MinAirTemp1 = new List<float>();
                    airHumid.measurements.MinAirTemp1.Add(rdr.GetFloat(6));

                    airHumid.measurements.AirTempQ = new List<float>();
                    airHumid.measurements.AirTempQ.Add(rdr.GetFloat(7));

                    airHumid.measurements.AirTemp2 = new List<float>();
                    airHumid.measurements.AirTemp2.Add(rdr.GetFloat(8));

                    airHumid.measurements.AirTemp2Q = new List<float>();
                    airHumid.measurements.AirTemp2Q.Add(rdr.GetFloat(9));

                    airHumid.measurements.RH = new List<float>();
                    airHumid.measurements.RH.Add(rdr.GetFloat(10));

                    airHumid.measurements.Dew_Point = new List<float>();
                    airHumid.measurements.Dew_Point.Add(rdr.GetFloat(11));



                    result.Add(airHumid);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetAtmosPressureList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;
            float atmPressure;

            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);
                    atmPressure = rdr.GetFloat(4);


                    var atmosPressure = new AtmosPressures();
                    atmosPressure.deviceId = rdr.GetInt32(2).ToString();

                    atmosPressure.measurements = new AtmosPressureMeasurements();


                    atmosPressure.measurements.TmStamp = new List<string>();
                    atmosPressure.measurements.TmStamp.Add(rdr.GetString(0));

                    atmosPressure.measurements.RecNum = new List<string>();
                    atmosPressure.measurements.RecNum.Add(rdr.GetString(1));



                    atmosPressure.measurements.Identifier = new List<float>();
                    atmosPressure.measurements.Identifier.Add(rdr.GetFloat(3));

                    atmosPressure.measurements.AtmPressure = new List<float>();
                    atmosPressure.measurements.AtmPressure.Add(rdr.GetFloat(4));


                    result.Add(atmosPressure);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetPavementList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;


            float pvmntTemp1;
            float pavementQ;
            float altPaveTemp1;
            float frzPntTemp1;
            float frzPntTemp1Q;
            float pvmnCond1;
            float pvmntCond1Q;
            float sbAsphltTemp;
            float pvBaseTemp1;
            float pvBaseTemp1Q;
            float pvmntSrfCvTh;
            float pvmntSrfCvThQ;

            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);

                    pvmntTemp1 = rdr.GetFloat(4);
                    pavementQ = rdr.GetFloat(5);
                    altPaveTemp1 = rdr.GetFloat(6);
                    frzPntTemp1 = rdr.GetFloat(7);
                    frzPntTemp1Q = rdr.GetFloat(8);
                    pvmnCond1 = rdr.GetFloat(9);
                    pvmntCond1Q = rdr.GetFloat(10);
                    sbAsphltTemp = rdr.GetFloat(11);
                    pvBaseTemp1 = rdr.GetFloat(12);
                    pvBaseTemp1Q = rdr.GetFloat(13);
                    pvmntSrfCvTh = rdr.GetFloat(14);
                    pvmntSrfCvThQ = rdr.GetFloat(15);

                    var airHumid = new Pavements();

                    airHumid.deviceId = rdr.GetInt32(2).ToString();
                    airHumid.measurements = new PavementMeasurements();

                    airHumid.measurements.TmStamp = new List<string>();
                    airHumid.measurements.TmStamp.Add(rdr.GetString(0));

                    airHumid.measurements.RecNum = new List<string>();
                    airHumid.measurements.RecNum.Add(rdr.GetString(1));



                    airHumid.measurements.Identifier = new List<float>();
                    airHumid.measurements.Identifier.Add(rdr.GetFloat(3));

                    airHumid.measurements.PvmntTemp1 = new List<float>();
                    airHumid.measurements.PvmntTemp1.Add(rdr.GetFloat(4));

                    airHumid.measurements.PavementQ = new List<float>();
                    airHumid.measurements.PavementQ.Add(rdr.GetFloat(5));

                    airHumid.measurements.AltPaveTemp1 = new List<float>();
                    airHumid.measurements.AltPaveTemp1.Add(rdr.GetFloat(6));

                    airHumid.measurements.FrzPntTemp1 = new List<float>();
                    airHumid.measurements.FrzPntTemp1.Add(rdr.GetFloat(7));

                    airHumid.measurements.FrzPntTemp1Q = new List<float>();
                    airHumid.measurements.FrzPntTemp1Q.Add(rdr.GetFloat(8));

                    airHumid.measurements.PvmnCond1 = new List<float>();
                    airHumid.measurements.PvmnCond1.Add(rdr.GetFloat(9));

                    airHumid.measurements.PvmntCond1Q = new List<float>();
                    airHumid.measurements.PvmntCond1Q.Add(rdr.GetFloat(10));

                    airHumid.measurements.SbAsphltTemp = new List<float>();
                    airHumid.measurements.SbAsphltTemp.Add(rdr.GetFloat(11));

                    airHumid.measurements.PvBaseTemp1 = new List<float>();
                    airHumid.measurements.PvBaseTemp1.Add(rdr.GetFloat(11));

                    airHumid.measurements.PvBaseTemp1Q = new List<float>();
                    airHumid.measurements.PvBaseTemp1Q.Add(rdr.GetFloat(11));

                    airHumid.measurements.PvmntSrfCvTh = new List<float>();
                    airHumid.measurements.PvmntSrfCvTh.Add(rdr.GetFloat(11));

                    airHumid.measurements.PvmntSrfCvThQ = new List<float>();
                    airHumid.measurements.PvmntSrfCvThQ.Add(rdr.GetFloat(11));


                    result.Add(airHumid);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetPrecipitationList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;


            float gaugeTot;
            float newPrecip;
            float hrlyPrecip;
            float precipGaugeQ;
            float precipDetRatio;
            float precipDetQ;

            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);
                    gaugeTot = rdr.GetFloat(4);
                    newPrecip = rdr.GetFloat(5);
                    hrlyPrecip = rdr.GetFloat(6);
                    precipGaugeQ = rdr.GetFloat(7);
                    precipDetRatio = rdr.GetFloat(8);
                    precipDetQ = rdr.GetFloat(9);


                    var airHumid = new Precipitations();


                    airHumid.deviceId = rdr.GetInt32(2).ToString();
                    airHumid.measurements = new PrecipitationMeasurements();

                    airHumid.measurements.TmStamp = new List<string>();
                    airHumid.measurements.TmStamp.Add(rdr.GetString(0));

                    airHumid.measurements.RecNum = new List<string>();
                    airHumid.measurements.RecNum.Add(rdr.GetString(1));



                    airHumid.measurements.Identifier = new List<float>();
                    airHumid.measurements.Identifier.Add(rdr.GetFloat(3));

                    airHumid.measurements.GaugeTot = new List<float>();
                    airHumid.measurements.GaugeTot.Add(rdr.GetFloat(4));

                    airHumid.measurements.NewPrecip = new List<float>();
                    airHumid.measurements.NewPrecip.Add(rdr.GetFloat(5));

                    airHumid.measurements.HrlyPrecip = new List<float>();
                    airHumid.measurements.HrlyPrecip.Add(rdr.GetFloat(6));

                    airHumid.measurements.PrecipGaugeQ = new List<float>();
                    airHumid.measurements.PrecipGaugeQ.Add(rdr.GetFloat(7));

                    airHumid.measurements.PrecipDetRatio = new List<float>();
                    airHumid.measurements.PrecipDetRatio.Add(rdr.GetFloat(8));

                    airHumid.measurements.PrecipDetQ = new List<float>();
                    airHumid.measurements.PrecipDetQ.Add(rdr.GetFloat(9));


                    result.Add(airHumid);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetSnowList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;
            float hS;
            float hStd;
            float hrlySnow;
            float snowQ;

            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);
                    hS = rdr.GetFloat(4);
                    hStd = rdr.GetFloat(5);
                    hrlySnow = rdr.GetFloat(6);
                    snowQ = rdr.GetFloat(7);


                    var airHumid = new Snows();

                    airHumid.deviceId = rdr.GetInt32(2).ToString();
                    airHumid.measurements = new SnowMeasurements();

                    airHumid.measurements.TmStamp = new List<string>();
                    airHumid.measurements.TmStamp.Add(rdr.GetString(0));

                    airHumid.measurements.RecNum = new List<string>();
                    airHumid.measurements.RecNum.Add(rdr.GetString(1));



                    airHumid.measurements.Identifier = new List<float>();
                    airHumid.measurements.Identifier.Add(rdr.GetFloat(3));

                    airHumid.measurements.HS = new List<float>();
                    airHumid.measurements.HS.Add(rdr.GetFloat(4));

                    airHumid.measurements.HStd = new List<float>();
                    airHumid.measurements.HStd.Add(rdr.GetFloat(5));

                    airHumid.measurements.HrlySnow = new List<float>();
                    airHumid.measurements.HrlySnow.Add(rdr.GetFloat(6));

                    airHumid.measurements.SnowQ = new List<float>();
                    airHumid.measurements.SnowQ.Add(rdr.GetFloat(7));


                    result.Add(airHumid);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetWindList(MySqlConnection con, MySqlDataReader rdr)
        {
            string tmStamp;
            string recNum;
            int stationId;
            float identifier;

            float maxWindSpd;
            float meanWindSpd;
            float windSpd;
            float windSpdQ;
            float meanWindDir;
            float stDevWind;
            float windDir;
            float derimeStat;


            List<StationDatas> result = new List<StationDatas>();
            try
            {
                while (rdr.Read())
                {
                    tmStamp = rdr.GetString(0);
                    recNum = rdr.GetString(1);
                    stationId = rdr.GetInt32(2);
                    identifier = rdr.GetFloat(3);
                    maxWindSpd = rdr.GetFloat(4);
                    meanWindSpd = rdr.GetFloat(5);
                    windSpd = rdr.GetFloat(6);
                    windSpdQ = rdr.GetFloat(7);
                    meanWindDir = rdr.GetFloat(8);
                    stDevWind = rdr.GetFloat(9);
                    windDir = rdr.GetFloat(10);
                    derimeStat = rdr.GetFloat(11);

                    var airHumid = new Winds();

                    airHumid.deviceId = rdr.GetInt32(2).ToString();
                    airHumid.measurements = new WindMeasurements();

                    airHumid.measurements.TmStamp = new List<string>();
                    airHumid.measurements.TmStamp.Add(rdr.GetString(0));

                    airHumid.measurements.RecNum = new List<string>();
                    airHumid.measurements.RecNum.Add(rdr.GetString(1));



                    airHumid.measurements.Identifier = new List<float>();
                    airHumid.measurements.Identifier.Add(rdr.GetFloat(3));

                    airHumid.measurements.MaxWindSpd = new List<float>();
                    airHumid.measurements.MaxWindSpd.Add(rdr.GetFloat(4));

                    airHumid.measurements.MeanWindSpd = new List<float>();
                    airHumid.measurements.MeanWindSpd.Add(rdr.GetFloat(5));

                    airHumid.measurements.WindSpd = new List<float>();
                    airHumid.measurements.WindSpd.Add(rdr.GetFloat(6));

                    airHumid.measurements.WindSpdQ = new List<float>();
                    airHumid.measurements.WindSpdQ.Add(rdr.GetFloat(7));

                    airHumid.measurements.MeanWindDir = new List<float>();
                    airHumid.measurements.MeanWindDir.Add(rdr.GetFloat(8));

                    airHumid.measurements.StDevWind = new List<float>();
                    airHumid.measurements.StDevWind.Add(rdr.GetFloat(9));

                    airHumid.measurements.WindDir = new List<float>();
                    airHumid.measurements.WindDir.Add(rdr.GetFloat(10));

                    airHumid.measurements.DerimeStat = new List<float>();
                    airHumid.measurements.DerimeStat.Add(rdr.GetFloat(11));


                    result.Add(airHumid);

                    //Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11}", tmStamp, recNum,
                    //        stationId, identifier, maxAirTemp1, curAirTemp1, minAirTemp1, airTempQ, airTemp2, airTemp2Q, rh, dewPoint);
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                //rdr.Close();
            }
            return result;

        }

        static List<StationDatas> GetDataFromTable(MySqlConnection con, string tableName)
        {
            var result = new List<StationDatas>();
            con.Open();
            //Console.WriteLine($"MySQL version : {con.ServerVersion}");
            string sql = "SELECT * FROM lndb." + tableName + " order by RecNum desc limit 1";
            using var cmd = new MySqlCommand(sql, con);
            using MySqlDataReader rdr = cmd.ExecuteReader();
            if (tableName.Contains("humidity"))
            {
                var ah_list = GetAirHumidityList(con, rdr);
                result = ah_list;
            }
            if (tableName.Contains("pressure"))
            {
                var ah_list = GetAtmosPressureList(con, rdr);
                result = ah_list;
            }
            if (tableName.Contains("pavement"))
            {
                var ah_list = GetPavementList(con, rdr);
                result = ah_list;
            }
            if (tableName.Contains("precipitation"))
            {
                var ah_list = GetPrecipitationList(con, rdr);
                result = ah_list;
            }
            if (tableName.Contains("snow"))
            {
                var ah_list = GetSnowList(con, rdr);
                result = ah_list;
            }
            if (tableName.Contains("wind"))
            {
                var ah_list = GetWindList(con, rdr);
                result = ah_list;
            }

            con.Close();
            return result;
        }

        //bruce test
        static List<List<StationDatas>> PullData(List<string> tableNames)
        {
            List<List<StationDatas>> result = new List<List<StationDatas>>();
            string cs = @"server=localhost;userid=dbuser;password=s$cret;database=testdb";

            cs = @"Server=pocmysql.mysql.database.azure.com;UserID=PoCAdminSQL;Password=WQZ2c6sQmtH3i6r;Database=lndb";

            using var con = new MySqlConnection(cs);

            foreach (var tableName in tableNames)
            {
                var data = GetDataFromTable(con, tableName);
                result.Add(data);

            }
            return result;
        }

        static List<string> GetTableNames()
        {
            string tableName = "";
            var result = new List<string>();
            string cs = @"server=localhost;userid=dbuser;password=s$cret;database=testdb";
            cs = @"Server=pocmysql.mysql.database.azure.com;UserID=PoCAdminSQL;Password=WQZ2c6sQmtH3i6r;Database=lndb";
            using var con = new MySqlConnection(cs);
            con.Open();
            Console.WriteLine($"Get Table Names...");

            string sql = "SELECT * FROM lndb.nels_irishman35094_air_humidity order by RecNum desc limit 1";


            sql = @"SELECT TABLE_NAME
                        FROM INFORMATION_SCHEMA.TABLES
                        WHERE TABLE_TYPE = 'BASE TABLE' and table_schema = 'lndb'";

            using var cmd = new MySqlCommand(sql, con);
            using MySqlDataReader rdr = cmd.ExecuteReader();
            try
            {
                while (rdr.Read())
                {
                    tableName = rdr.GetString(0);
                    if (!tableName.Contains("meta"))
                    {
                        result.Add(tableName);
                    }
                }
            }
            catch (Exception exe)
            {
                Console.WriteLine(exe.Message);
            }
            finally
            {
                rdr.Close();
            }
            con.Close();

            return result;
        }
        static List<WeatherData1> ConverToJson(List<List<StationDatas>> stationData, List<string> tableNames)
        {


            List<StationDatas> result = new List<StationDatas>();


            for (var i = 0; i < stationData.Count; i++)
            {
                if (tableNames[i].Contains("humidity"))
                {
                    var ahs = new AirHumidities();
                    ahs.measurements = new AirHumidityMeasurements();
                    //ahs.device.DeviceId = "nels_irishman35094_air_humidity";
                    int stationId = 0;
                    foreach (var airHumidity in stationData[i])
                    {
                        //var tempData = airHumidity as AirHumidity;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.MaxAirTemp1.Add(tempData.MaxAirTemp1);
                        //ahs.measurements.CurAirTemp1.Add(tempData.CurAirTemp1);
                        //ahs.measurements.MinAirTemp1.Add(tempData.MinAirTemp1);
                        //ahs.measurements.AirTempQ.Add(tempData.AirTempQ);
                        //ahs.measurements.AirTemp2.Add(tempData.AirTemp2);
                        //ahs.measurements.AirTemp2Q.Add(tempData.AirTemp2Q);
                        //ahs.measurements.RH.Add(tempData.Rh);
                        //ahs.measurements.Dew_Point.Add(tempData.DewPoint);
                        //stationId = tempData.StationId;

                        ahs = airHumidity as AirHumidities;
                        stationId = Convert.ToInt32(ahs.deviceId);



                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);



                }

                if (tableNames[i].Contains("pressure"))
                {
                    var ahs = new AtmosPressures();
                    ahs.measurements = new AtmosPressureMeasurements();
                    int stationId = 0;
                    foreach (var atmosPressure in stationData[i])
                    {
                        //var tempData = atmosPressure as AtmosPressure;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.AtmPressure.Add(tempData.AtmPressure);
                        //stationId = tempData.StationId;

                        ahs = atmosPressure as AtmosPressures;
                        stationId = Convert.ToInt32(ahs.deviceId);
                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);

                }

                if (tableNames[i].Contains("pavement"))
                {
                    var ahs = new Pavements();
                    ahs.measurements = new PavementMeasurements();
                    //ahs.device.DeviceId = "nels_irishman35094_air_humidity";
                    int stationId = 0;
                    foreach (var airHumidity in stationData[i])
                    {
                        //var tempData = airHumidity as Pavement;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.PvmntTemp1.Add(tempData.PvmntTemp1);
                        //ahs.measurements.PavementQ.Add(tempData.PavementQ);
                        //ahs.measurements.AltPaveTemp1.Add(tempData.AltPaveTemp1);
                        //ahs.measurements.FrzPntTemp1.Add(tempData.FrzPntTemp1);
                        //ahs.measurements.FrzPntTemp1Q.Add(tempData.FrzPntTemp1Q);
                        //ahs.measurements.PvmnCond1.Add(tempData.PvmnCond1);
                        //ahs.measurements.PvmntCond1Q.Add(tempData.PvmntCond1Q);
                        //ahs.measurements.SbAsphltTemp.Add(tempData.SbAsphltTemp);
                        //ahs.measurements.PvBaseTemp1.Add(tempData.PvBaseTemp1);
                        //ahs.measurements.PvBaseTemp1Q.Add(tempData.PvBaseTemp1Q);
                        //ahs.measurements.PvmntSrfCvTh.Add(tempData.PvmntSrfCvTh);
                        //ahs.measurements.PvmntSrfCvThQ.Add(tempData.PvmntSrfCvThQ);
                        //stationId = tempData.StationId;

                        ahs = airHumidity as Pavements;
                        stationId = Convert.ToInt32(ahs.deviceId);
                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);

                }

                if (tableNames[i].Contains("precipitation"))
                {
                    var ahs = new Precipitations();
                    ahs.measurements = new PrecipitationMeasurements();
                    //ahs.device.DeviceId = "nels_irishman35094_air_humidity";
                    int stationId = 0;
                    foreach (var airHumidity in stationData[i])
                    {
                        //var tempData = airHumidity as Precipitation;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.GaugeTot.Add(tempData.GaugeTot);
                        //ahs.measurements.NewPrecip.Add(tempData.NewPrecip);
                        //ahs.measurements.HrlyPrecip.Add(tempData.HrlyPrecip);
                        //ahs.measurements.PrecipGaugeQ.Add(tempData.PrecipGaugeQ);
                        //ahs.measurements.PrecipDetRatio.Add(tempData.PrecipDetRatio);
                        //ahs.measurements.PrecipDetQ.Add(tempData.PrecipDetQ);

                        //stationId = tempData.StationId;

                        ahs = airHumidity as Precipitations;
                        stationId = Convert.ToInt32(ahs.deviceId);
                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);

                }

                if (tableNames[i].Contains("snow"))
                {
                    var ahs = new Snows();
                    ahs.measurements = new SnowMeasurements();
                    //ahs.device.DeviceId = "nels_irishman35094_air_humidity";
                    int stationId = 0;
                    foreach (var airHumidity in stationData[i])
                    {
                        //var tempData = airHumidity as Snow;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.HS.Add(tempData.HS);
                        //ahs.measurements.HStd.Add(tempData.HStd);
                        //ahs.measurements.HrlySnow.Add(tempData.HrlySnow);
                        //ahs.measurements.SnowQ.Add(tempData.SnowQ);

                        //stationId = tempData.StationId;

                        ahs = airHumidity as Snows;
                        stationId = Convert.ToInt32(ahs.deviceId);
                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);

                }

                if (tableNames[i].Contains("wind"))
                {
                    var ahs = new Winds();
                    ahs.measurements = new WindMeasurements();
                    //ahs.device.DeviceId = "nels_irishman35094_air_humidity";
                    int stationId = 0;
                    foreach (var airHumidity in stationData[i])
                    {
                        //var tempData = airHumidity as Wind;
                        //ahs.measurements.TmStamp.Add(tempData.TmStamp);
                        //ahs.measurements.RecNum.Add(tempData.RecNum);
                        //ahs.measurements.StationID.Add(tempData.StationId);
                        //ahs.measurements.Identifier.Add(tempData.Identifier);
                        //ahs.measurements.MaxWindSpd.Add(tempData.MaxWindSpd);
                        //ahs.measurements.MeanWindSpd.Add(tempData.MeanWindSpd);
                        //ahs.measurements.WindSpd.Add(tempData.WindSpd);
                        //ahs.measurements.WindSpdQ.Add(tempData.WindSpdQ);
                        //ahs.measurements.MeanWindDir.Add(tempData.MeanWindDir);
                        //ahs.measurements.StDevWind.Add(tempData.StDevWind);
                        //ahs.measurements.WindDir.Add(tempData.WindDir);
                        //ahs.measurements.DerimeStat.Add(tempData.DerimeStat);

                        //stationId = tempData.StationId;

                        ahs = airHumidity as Winds;
                        stationId = Convert.ToInt32(ahs.deviceId);
                    }
                    ahs.deviceId = stationId.ToString();
                    string obj = JsonConvert.SerializeObject(ahs);

                    result.Add(ahs as StationDatas);

                }
            }



            //bruce test here
            int count = tableNames.Count / 6;
            List<WeatherData> wdl = new List<WeatherData>();
            List<WeatherData1> wdl1 = new List<WeatherData1>();
            for (var i = 0; i < result.Count; i += 6)
            {
                WeatherData wd = new WeatherData();
                WeatherData1 wd1 = new WeatherData1();

                wd.airHumidity = result[i] as StationDatas;
                wd.atmosPressure = result[i + 1] as StationDatas;
                wd.pavement = result[i + 2] as StationDatas;
                wd.precipitation = result[i + 3] as StationDatas;
                wd.snow = result[i + 4] as StationDatas;
                wd.wind = result[i + 5] as StationDatas;


                
                var temp = wd.airHumidity as AirHumidities;
                wd1.deviceId = temp.deviceId;
                wd1.measurements = new Measurements();
                wd1.measurements.timestamp = temp.measurements.TmStamp[0];
                wd1.measurements.maxAirTemp = temp.measurements.MaxAirTemp1[0];
                wd1.measurements.currentAirTemp = temp.measurements.CurAirTemp1[0];
                wd1.measurements.minAirTemp = temp.measurements.MinAirTemp1[0];
                wd1.measurements.airTempQuality = temp.measurements.AirTempQ[0];
                wd1.measurements.airTempAlternate = temp.measurements.AirTemp2[0];
                wd1.measurements.airTempAlternateQuality = temp.measurements.AirTemp2Q[0];
                wd1.measurements.relativeHumidity = temp.measurements.RH[0];
                wd1.measurements.dewPoint = temp.measurements.Dew_Point[0];

                var temp1 = wd.atmosPressure as AtmosPressures;

                wd1.measurements.atmospherePressure = temp1.measurements.AtmPressure[0];


                var temp2 = wd.pavement as Pavements;

                wd1.measurements.pavementTemp = temp2.measurements.PvmntTemp1[0];
                wd1.measurements.pavementTempQuality = temp2.measurements.PavementQ[0];
                wd1.measurements.alternatePavementTemp = temp2.measurements.AltPaveTemp1[0];
                wd1.measurements.freezePointTemp = temp2.measurements.FrzPntTemp1[0];
                wd1.measurements.freezePointTempQuality = temp2.measurements.FrzPntTemp1Q[0];
                wd1.measurements.pavementCondition = temp2.measurements.PvmnCond1[0];
                wd1.measurements.pavementConditionQuality = temp2.measurements.PvmntCond1Q[0];
                wd1.measurements.subAsphaltTemp = temp2.measurements.SbAsphltTemp[0];
                wd1.measurements.pavementBaseTemp = temp2.measurements.PvBaseTemp1[0];
                wd1.measurements.pavementBaseTempQuality = temp2.measurements.PvBaseTemp1Q[0];
                wd1.measurements.pavementSurfaceConductivity = temp2.measurements.PvmntSrfCvTh[0];
                wd1.measurements.pavementSurfaceConductivityQuality = temp2.measurements.PvmntSrfCvThQ[0];


                var temp3 = wd.wind as Winds;
                wd1.measurements.maxWindSpeed = temp3.measurements.MaxWindSpd[0];
                wd1.measurements.meanWindSpeed = temp3.measurements.MeanWindSpd[0];
                wd1.measurements.windSpeed = temp3.measurements.WindSpd[0];
                wd1.measurements.windSpeedQuality = temp3.measurements.WindSpdQ[0];
                wd1.measurements.meanWindDirection = temp3.measurements.MeanWindDir[0];
                wd1.measurements.standardWindDeviation = temp3.measurements.StDevWind[0];
                wd1.measurements.windDirection = temp3.measurements.WindDir[0];

                wdl.Add(wd);
                wdl1.Add(wd1);
            }


            //return wdl;
            return wdl1;

 

        }
    }





    //////////////////////////////
    /// <summary>
    /// Defines an interface that must be implemented by custom components responsible for detecting specific transient conditions.
    /// </summary>
    public interface ITransientErrorDetectionStrategy
    {
        /// <summary>
        /// Determines whether the specified exception represents a transient failure that can be compensated by a retry.
        /// </summary>
        /// <param name="ex">The exception object to be verified.</param>
        /// <returns>true if the specified exception is considered as transient; otherwise, false.</returns>
        bool IsTransient(Exception ex);
    }

    /// <summary>
    /// An error detection strategy that delegates the detection to a lambda.
    /// </summary>
    public class DelegateErrorDetectionStrategy : ITransientErrorDetectionStrategy
    {
        readonly Func<Exception, bool> underlying;

        public DelegateErrorDetectionStrategy(Func<Exception, bool> isTransient)
        {
            this.underlying = Preconditions.CheckNotNull(isTransient);
        }

        public bool IsTransient(Exception ex) => this.underlying(ex);
    }

    /// <summary>
    /// Represents a retry strategy that determines the number of retry attempts and the interval between retries.
    /// </summary>
    public abstract class RetryStrategy
    {
        /// <summary>
        /// Represents the default number of retry attempts.
        /// </summary>
        public static readonly int DefaultClientRetryCount = 10;

        /// <summary>
        /// Represents the default amount of time used when calculating a random delta in the exponential delay between retries.
        /// </summary>
        public static readonly TimeSpan DefaultClientBackoff = TimeSpan.FromSeconds(10.0);

        /// <summary>
        /// Represents the default maximum amount of time used when calculating the exponential delay between retries.
        /// </summary>
        public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30.0);

        /// <summary>
        /// Represents the default minimum amount of time used when calculating the exponential delay between retries.
        /// </summary>
        public static readonly TimeSpan DefaultMinBackoff = TimeSpan.FromSeconds(1.0);

        /// <summary>
        /// Represents the default interval between retries.
        /// </summary>
        public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(1.0);

        /// <summary>
        /// Represents the default time increment between retry attempts in the progressive delay policy.
        /// </summary>
        public static readonly TimeSpan DefaultRetryIncrement = TimeSpan.FromSeconds(1.0);

        /// <summary>
        /// Represents the default flag indicating whether the first retry attempt will be made immediately,
        /// whereas subsequent retries will remain subject to the retry interval.
        /// </summary>
        public static readonly bool DefaultFirstFastRetry = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy" /> class.
        /// </summary>
        /// <param name="firstFastRetry">true to immediately retry in the first attempt; otherwise, false. The subsequent retries will remain subject to the configured retry interval.</param>
        protected RetryStrategy(bool firstFastRetry)
        {
            this.FastFirstRetry = firstFastRetry;
        }

        /// <summary>
        /// Returns a default policy that performs no retries, but invokes the action only once.
        /// </summary>
        public static RetryStrategy NoRetry { get; } = new FixedInterval(0, DefaultRetryInterval);

        /// <summary>
        /// Returns a default policy that implements a fixed retry interval configured with the <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultClientRetryCount" /> and <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultRetryInterval" /> parameters.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryStrategy DefaultFixed { get; } = new FixedInterval(DefaultClientRetryCount, DefaultRetryInterval);

        /// <summary>
        /// Returns a default policy that implements a progressive retry interval configured with the <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultClientRetryCount" />, <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultRetryInterval" />, and <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultRetryIncrement" /> parameters.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryStrategy DefaultProgressive { get; } = new Incremental(DefaultClientRetryCount, DefaultRetryInterval, DefaultRetryIncrement);

        /// <summary>
        /// Returns a default policy that implements a random exponential retry interval configured with the <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultClientRetryCount" />, <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultMinBackoff" />, <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultMaxBackoff" />, and <see cref="F:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryStrategy.DefaultClientBackoff" /> parameters.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryStrategy DefaultExponential { get; } = new ExponentialBackoff(DefaultClientRetryCount, DefaultMinBackoff, DefaultMaxBackoff, DefaultClientBackoff);

        /// <summary>
        /// Gets or sets a value indicating whether the first retry attempt will be made immediately,
        /// whereas subsequent retries will remain subject to the retry interval.
        /// </summary>
        public bool FastFirstRetry { get; set; }

        /// <summary>
        /// Returns the corresponding ShouldRetry delegate.
        /// </summary>
        /// <returns>The ShouldRetry delegate.</returns>
        public abstract ShouldRetry GetShouldRetry();
    }

    /// <summary>
    /// A retry strategy with back-off parameters for calculating the exponential delay between retries.
    /// Note: this fixes an overflow in the stock ExponentialBackoff in the Transient Fault Handling library
    /// which causes the calculated delay to go negative.
    /// Use of this class for exponential backoff is encouraged instead.
    /// </summary>
    public class ExponentialBackoff : RetryStrategy
    {
        readonly int retryCount;
        readonly TimeSpan minBackoff;
        readonly TimeSpan maxBackoff;
        readonly TimeSpan deltaBackoff;

        public ExponentialBackoff()
            : this(DefaultClientRetryCount, DefaultMinBackoff, DefaultMaxBackoff, DefaultClientBackoff)
        {
        }

        public ExponentialBackoff(int retryCount, TimeSpan minBackoff, TimeSpan maxBackoff, TimeSpan deltaBackoff)
            : this(retryCount, minBackoff, maxBackoff, deltaBackoff, DefaultFirstFastRetry)
        {
        }

        public ExponentialBackoff(int retryCount, TimeSpan minBackoff, TimeSpan maxBackoff, TimeSpan deltaBackoff, bool firstFastRetry)
            : base(firstFastRetry)
        {
            Guard.ArgumentNotNegativeValue(retryCount, "retryCount");
            Guard.ArgumentNotNegativeValue(minBackoff.Ticks, "minBackoff");
            Guard.ArgumentNotNegativeValue(maxBackoff.Ticks, "minBackoff");
            Guard.ArgumentNotNegativeValue(deltaBackoff.Ticks, "deltaBackoff");
            Guard.ArgumentNotGreaterThan(minBackoff.TotalMilliseconds, maxBackoff.TotalMilliseconds, "minBackoff must be less than or equal to maxBackoff");
            this.retryCount = retryCount;
            this.minBackoff = minBackoff;
            this.maxBackoff = maxBackoff;
            this.deltaBackoff = deltaBackoff;
        }

        public override ShouldRetry GetShouldRetry()
        {
            return (int currentRetryCount, Exception lastException, out TimeSpan retryInterval) =>
            {
                if (currentRetryCount < this.retryCount)
                {
                    var random = new Random();
                    double length = Math.Min(
                        this.minBackoff.TotalMilliseconds + (Math.Pow(2.0, currentRetryCount) - 1.0) * (0.8 + random.NextDouble() * 0.4) * this.deltaBackoff.TotalMilliseconds,
                        this.maxBackoff.TotalMilliseconds);
                    retryInterval = TimeSpan.FromMilliseconds(length);
                    return true;
                }
                else
                {
                    retryInterval = TimeSpan.Zero;
                    return false;
                }
            };
        }
    }

    /// <summary>
    /// Defines a callback delegate that will be invoked whenever a retry condition is encountered.
    /// </summary>
    /// <param name="retryCount">The current retry attempt count.</param>
    /// <param name="lastException">The exception that caused the retry conditions to occur.</param>
    /// <param name="delay">The delay that indicates how long the current thread will be suspended before the next iteration is invoked.</param>
    /// <returns><see langword="true" /> if a retry is allowed; otherwise, <see langword="false" />.</returns>
    public delegate bool ShouldRetry(int retryCount, Exception lastException, out TimeSpan delay);

    /// <summary>
    /// Implements the common guard methods.
    /// </summary>
    static class Guard
    {
        /// <summary>
        /// Checks a string argument to ensure that it isn't null or empty.
        /// </summary>
        /// <param name="argumentValue">The argument value to check.</param>
        /// <param name="argumentName">The name of the argument.</param>
        /// <returns>The return value should be ignored. It is intended to be used only when validating arguments during instance creation (for example, when calling the base constructor).</returns>
        public static bool ArgumentNotNullOrEmptyString(string argumentValue, string argumentName)
        {
            ArgumentNotNull(argumentValue, argumentName);
            if (argumentValue.Length == 0)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "String {0} cannot be empty",
                        new object[]
                        {
                            argumentName
                        }));
            }

            return true;
        }

        /// <summary>
        /// Checks an argument to ensure that it isn't null.
        /// </summary>
        /// <param name="argumentValue">The argument value to check.</param>
        /// <param name="argumentName">The name of the argument.</param>
        /// <returns>The return value should be ignored. It is intended to be used only when validating arguments during instance creation (for example, when calling the base constructor).</returns>
        public static bool ArgumentNotNull(object argumentValue, string argumentName)
        {
            if (argumentValue == null)
            {
                throw new ArgumentNullException(argumentName);
            }

            return true;
        }

        /// <summary>
        /// Checks an argument to ensure that its 32-bit signed value isn't negative.
        /// </summary>
        /// <param name="argumentValue">The <see cref="T:System.Int32" /> value of the argument.</param>
        /// <param name="argumentName">The name of the argument for diagnostic purposes.</param>
        public static void ArgumentNotNegativeValue(int argumentValue, string argumentName)
        {
            if (argumentValue < 0)
            {
                throw new ArgumentOutOfRangeException(
                    argumentName,
                    argumentValue,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Argument {0} cannot be negative",
                        new object[]
                        {
                            argumentName
                        }));
            }
        }

        /// <summary>
        /// Checks an argument to ensure that its 64-bit signed value isn't negative.
        /// </summary>
        /// <param name="argumentValue">The <see cref="T:System.Int64" /> value of the argument.</param>
        /// <param name="argumentName">The name of the argument for diagnostic purposes.</param>
        public static void ArgumentNotNegativeValue(long argumentValue, string argumentName)
        {
            if (argumentValue < 0L)
            {
                throw new ArgumentOutOfRangeException(
                    argumentName,
                    argumentValue,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Argument {0} cannot be negative",
                        new object[]
                        {
                            argumentName
                        }));
            }
        }

        /// <summary>
        /// Checks an argument to ensure that its value doesn't exceed the specified ceiling baseline.
        /// </summary>
        /// <param name="argumentValue">The <see cref="T:System.Double" /> value of the argument.</param>
        /// <param name="ceilingValue">The <see cref="T:System.Double" /> ceiling value of the argument.</param>
        /// <param name="argumentName">The name of the argument for diagnostic purposes.</param>
        public static void ArgumentNotGreaterThan(double argumentValue, double ceilingValue, string argumentName)
        {
            if (argumentValue > ceilingValue)
            {
                throw new ArgumentOutOfRangeException(
                    argumentName,
                    argumentValue,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Argument {0} cannot be greater than baseline value {1}",
                        new object[]
                        {
                            argumentName,
                            ceilingValue
                        }));
            }
        }
    }

    public class AtomicBoolean
    {
        int underlying;

        public AtomicBoolean(bool value)
        {
            this.underlying = value ? 1 : 0;
        }

        public AtomicBoolean()
            : this(false)
        {
        }

        public static implicit operator bool(AtomicBoolean value) => value.Get();

        public bool Get() => Interlocked.Exchange(ref this.underlying, this.underlying) != 0;

        public void Set(bool value) => Interlocked.Exchange(ref this.underlying, value ? 1 : 0);

        public bool GetAndSet(bool value) => Interlocked.Exchange(ref this.underlying, value ? 1 : 0) != 0;

        public bool CompareAndSet(bool expected, bool result)
        {
            int e = expected ? 1 : 0;
            int r = result ? 1 : 0;
            return Interlocked.CompareExchange(ref this.underlying, r, e) == e;
        }
    }

    //
    // Summary:
    //     Transport types supported by DeviceClient - AMQP/TCP, HTTP 1.1, MQTT/TCP, AMQP/WS,
    //     MQTT/WS
    public enum TransportType
    {
        //
        // Summary:
        //     Advanced Message Queuing Protocol transport. Try Amqp over TCP first and fallback
        //     to Amqp over WebSocket if that fails
        Amqp = 0,
        //
        // Summary:
        //     HyperText Transfer Protocol version 1 transport.
        Http1 = 1,
        //
        // Summary:
        //     Advanced Message Queuing Protocol transport over WebSocket only.
        Amqp_WebSocket_Only = 2,
        //
        // Summary:
        //     Advanced Message Queuing Protocol transport over native TCP only
        Amqp_Tcp_Only = 3,
        //
        // Summary:
        //     Message Queuing Telemetry Transport. Try Mqtt over TCP first and fallback to
        //     Mqtt over WebSocket if that fails
        Mqtt = 4,
        //
        // Summary:
        //     Message Queuing Telemetry Transport over Websocket only.
        Mqtt_WebSocket_Only = 5,
        //
        // Summary:
        //     Message Queuing Telemetry Transport over native TCP only
        Mqtt_Tcp_Only = 6
    }

    /// <summary>
    /// Provides the base implementation of the retry mechanism for unreliable actions and transient conditions.
    /// </summary>
    public class RetryPolicy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy" /> class with the specified number of retry attempts and parameters defining the progressive delay between retries.
        /// </summary>
        /// <param name="errorDetectionStrategy">The <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ITransientErrorDetectionStrategy" /> that is responsible for detecting transient conditions.</param>
        /// <param name="retryStrategy">The strategy to use for this retry policy.</param>
        public RetryPolicy(ITransientErrorDetectionStrategy errorDetectionStrategy, RetryStrategy retryStrategy)
        {
            Guard.ArgumentNotNull(errorDetectionStrategy, "errorDetectionStrategy");
            Guard.ArgumentNotNull(retryStrategy, "retryPolicy");
            this.ErrorDetectionStrategy = errorDetectionStrategy;
            if (errorDetectionStrategy == null)
            {
                throw new InvalidOperationException("The error detection strategy type must implement the ITransientErrorDetectionStrategy interface.");
            }

            this.RetryStrategy = retryStrategy;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy" /> class with the specified number of retry attempts and default fixed time interval between retries.
        /// </summary>
        /// <param name="errorDetectionStrategy">The <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ITransientErrorDetectionStrategy" /> that is responsible for detecting transient conditions.</param>
        /// <param name="retryCount">The number of retry attempts.</param>
        public RetryPolicy(ITransientErrorDetectionStrategy errorDetectionStrategy, int retryCount)
            : this(errorDetectionStrategy, new FixedInterval(retryCount))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy" /> class with the specified number of retry attempts and fixed time interval between retries.
        /// </summary>
        /// <param name="errorDetectionStrategy">The <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ITransientErrorDetectionStrategy" /> that is responsible for detecting transient conditions.</param>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="retryInterval">The interval between retries.</param>
        public RetryPolicy(ITransientErrorDetectionStrategy errorDetectionStrategy, int retryCount, TimeSpan retryInterval)
            : this(errorDetectionStrategy, new FixedInterval(retryCount, retryInterval))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy" /> class with the specified number of retry attempts and backoff parameters for calculating the exponential delay between retries.
        /// </summary>
        /// <param name="errorDetectionStrategy">The <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ITransientErrorDetectionStrategy" /> that is responsible for detecting transient conditions.</param>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="minBackoff">The minimum backoff time.</param>
        /// <param name="maxBackoff">The maximum backoff time.</param>
        /// <param name="deltaBackoff">The time value that will be used to calculate a random delta in the exponential delay between retries.</param>
        public RetryPolicy(ITransientErrorDetectionStrategy errorDetectionStrategy, int retryCount, TimeSpan minBackoff, TimeSpan maxBackoff, TimeSpan deltaBackoff)
            : this(errorDetectionStrategy, new ExponentialBackoff(retryCount, minBackoff, maxBackoff, deltaBackoff))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy" /> class with the specified number of retry attempts and parameters defining the progressive delay between retries.
        /// </summary>
        /// <param name="errorDetectionStrategy">The <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ITransientErrorDetectionStrategy" /> that is responsible for detecting transient conditions.</param>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="initialInterval">The initial interval that will apply for the first retry.</param>
        /// <param name="increment">The incremental time value that will be used to calculate the progressive delay between retries.</param>
        public RetryPolicy(ITransientErrorDetectionStrategy errorDetectionStrategy, int retryCount, TimeSpan initialInterval, TimeSpan increment)
            : this(errorDetectionStrategy, new Incremental(retryCount, initialInterval, increment))
        {
        }

        /// <summary>
        /// An instance of a callback delegate that will be invoked whenever a retry condition is encountered.
        /// </summary>
        public event EventHandler<RetryingEventArgs> Retrying;

        /// <summary>
        /// Returns a default policy that performs no retries, but invokes the action only once.
        /// </summary>
        public static RetryPolicy NoRetry { get; } = new RetryPolicy(new TransientErrorIgnoreStrategy(), RetryStrategy.NoRetry);

        /// <summary>
        /// Returns a default policy that implements a fixed retry interval configured with the default <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> retry strategy.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryPolicy DefaultFixed { get; } = new RetryPolicy(new TransientErrorCatchAllStrategy(), RetryStrategy.DefaultFixed);

        /// <summary>
        /// Returns a default policy that implements a progressive retry interval configured with the default <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.Incremental" /> retry strategy.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryPolicy DefaultProgressive { get; } = new RetryPolicy(new TransientErrorCatchAllStrategy(), RetryStrategy.DefaultProgressive);

        /// <summary>
        /// Returns a default policy that implements a random exponential retry interval configured with the default <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> retry strategy.
        /// The default retry policy treats all caught exceptions as transient errors.
        /// </summary>
        public static RetryPolicy DefaultExponential { get; } = new RetryPolicy(new TransientErrorCatchAllStrategy(), RetryStrategy.DefaultExponential);

        /// <summary>
        /// Gets the retry strategy.
        /// </summary>
        public RetryStrategy RetryStrategy { get; }

        /// <summary>
        /// Gets the instance of the error detection strategy.
        /// </summary>
        public ITransientErrorDetectionStrategy ErrorDetectionStrategy { get; }

        /// <summary>
        /// Repetitively executes the specified action while it satisfies the current retry policy.
        /// </summary>
        /// <param name="action">A delegate that represents the executable action that doesn't return any results.</param>
        public virtual void ExecuteAction(Action action)
        {
            Guard.ArgumentNotNull(action, "action");
            this.ExecuteAction<object>(
                delegate
                {
                    action();
                    return null;
                });
        }

        /// <summary>
        /// Repetitively executes the specified action while it satisfies the current retry policy.
        /// </summary>
        /// <typeparam name="TResult">The type of result expected from the executable action.</typeparam>
        /// <param name="func">A delegate that represents the executable action that returns the result of type <typeparamref name="TResult" />.</param>
        /// <returns>The result from the action.</returns>
        public virtual TResult ExecuteAction<TResult>(Func<TResult> func)
        {
            Guard.ArgumentNotNull(func, "func");
            int num = 0;
            ShouldRetry shouldRetry = this.RetryStrategy.GetShouldRetry();
            TResult result;
            while (true)
            {
                Exception ex;
                TimeSpan zero;
                try
                {
                    result = func();
                    break;
                }
#pragma warning disable CS0618 // Type or member is obsolete
                catch (RetryLimitExceededException ex2)
#pragma warning restore CS0618 // Type or member is obsolete
                {
                    if (ex2.InnerException != null)
                    {
                        throw ex2.InnerException;
                    }

                    result = default(TResult);
                    break;
                }
                catch (Exception ex3)
                {
                    ex = ex3;
                    if (!this.ErrorDetectionStrategy.IsTransient(ex) || !shouldRetry(num++, ex, out zero))
                    {
                        throw;
                    }
                }

                if (zero.TotalMilliseconds < 0.0)
                {
                    zero = TimeSpan.Zero;
                }

                this.OnRetrying(num, ex, zero);
                if (num > 1 || !this.RetryStrategy.FastFirstRetry)
                {
                    Task.Delay(zero).Wait();
                }
            }

            return result;
        }

        /// <summary>
        /// Repetitively executes the specified asynchronous task while it satisfies the current retry policy.
        /// </summary>
        /// <param name="taskAction">A function that returns a started task (also known as "hot" task).</param>
        /// <returns>
        /// A task that will run to completion if the original task completes successfully (either the
        /// first time or after retrying transient failures). If the task fails with a non-transient error or
        /// the retry limit is reached, the returned task will transition to a faulted state and the exception must be observed.
        /// </returns>
        public Task ExecuteAsync(Func<Task> taskAction)
        {
            return this.ExecuteAsync(taskAction, default(CancellationToken));
        }

        /// <summary>
        /// Repetitively executes the specified asynchronous task while it satisfies the current retry policy.
        /// </summary>
        /// <param name="taskAction">A function that returns a started task (also known as "hot" task).</param>
        /// <param name="cancellationToken">The token used to cancel the retry operation. This token does not cancel the execution of the asynchronous task.</param>
        /// <returns>
        /// Returns a task that will run to completion if the original task completes successfully (either the
        /// first time or after retrying transient failures). If the task fails with a non-transient error or
        /// the retry limit is reached, the returned task will transition to a faulted state and the exception must be observed.
        /// </returns>
        public Task ExecuteAsync(Func<Task> taskAction, CancellationToken cancellationToken)
        {
            if (taskAction == null)
            {
                throw new ArgumentNullException(nameof(taskAction));
            }

            return new AsyncExecution(taskAction, this.RetryStrategy.GetShouldRetry(), new Func<Exception, bool>(this.ErrorDetectionStrategy.IsTransient), new Action<int, Exception, TimeSpan>(this.OnRetrying), this.RetryStrategy.FastFirstRetry, cancellationToken).ExecuteAsync();
        }

        /// <summary>
        /// Repeatedly executes the specified asynchronous task while it satisfies the current retry policy.
        /// </summary>
        /// <param name="taskFunc">A function that returns a started task (also known as "hot" task).</param>
        /// <returns>
        /// Returns a task that will run to completion if the original task completes successfully (either the
        /// first time or after retrying transient failures). If the task fails with a non-transient error or
        /// the retry limit is reached, the returned task will transition to a faulted state and the exception must be observed.
        /// </returns>
        public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> taskFunc)
        {
            return this.ExecuteAsync(taskFunc, default(CancellationToken));
        }

        /// <summary>
        /// Repeatedly executes the specified asynchronous task while it satisfies the current retry policy.
        /// </summary>
        /// <param name="taskFunc">A function that returns a started task (also known as "hot" task).</param>
        /// <param name="cancellationToken">The token used to cancel the retry operation. This token does not cancel the execution of the asynchronous task.</param>
        /// <returns>
        /// Returns a task that will run to completion if the original task completes successfully (either the
        /// first time or after retrying transient failures). If the task fails with a non-transient error or
        /// the retry limit is reached, the returned task will transition to a faulted state and the exception must be observed.
        /// </returns>
        public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> taskFunc, CancellationToken cancellationToken)
        {
            if (taskFunc == null)
            {
                throw new ArgumentNullException(nameof(taskFunc));
            }

            return new AsyncExecution<TResult>(taskFunc, this.RetryStrategy.GetShouldRetry(), new Func<Exception, bool>(this.ErrorDetectionStrategy.IsTransient), new Action<int, Exception, TimeSpan>(this.OnRetrying), this.RetryStrategy.FastFirstRetry, cancellationToken).ExecuteAsync();
        }

        /// <summary>
        /// Notifies the subscribers whenever a retry condition is encountered.
        /// </summary>
        /// <param name="retryCount">The current retry attempt count.</param>
        /// <param name="lastError">The exception that caused the retry conditions to occur.</param>
        /// <param name="delay">The delay that indicates how long the current thread will be suspended before the next iteration is invoked.</param>
        protected virtual void OnRetrying(int retryCount, Exception lastError, TimeSpan delay)
        {
            this.Retrying?.Invoke(this, new RetryingEventArgs(retryCount, lastError));
        }

        /// <summary>
        /// Implements a strategy that treats all exceptions as transient errors.
        /// </summary>
        sealed class TransientErrorCatchAllStrategy : ITransientErrorDetectionStrategy
        {
            /// <summary>
            /// Always returns true.
            /// </summary>
            /// <param name="ex">The exception.</param>
            /// <returns>Always true.</returns>
            public bool IsTransient(Exception ex)
            {
                return true;
            }
        }

        /// <summary>
        /// Implements a strategy that ignores any transient errors.
        /// </summary>
        sealed class TransientErrorIgnoreStrategy : ITransientErrorDetectionStrategy
        {
            /// <summary>
            /// Always returns false.
            /// </summary>
            /// <param name="ex">The exception.</param>
            /// <returns>Always false.</returns>
            public bool IsTransient(Exception ex)
            {
                return false;
            }
        }
    }


    public struct Option<T> : IEquatable<Option<T>>
    {
        internal Option(T value, bool hasValue)
        {
            this.Value = value;
            this.HasValue = hasValue;
        }

        public bool HasValue { get; }

        T Value { get; }

        [Pure]
        public static bool operator ==(Option<T> opt1, Option<T> opt2) => opt1.Equals(opt2);

        [Pure]
        public static bool operator !=(Option<T> opt1, Option<T> opt2) => !opt1.Equals(opt2);

        [Pure]
        public bool Equals(Option<T> other)
        {
            if (!this.HasValue && !other.HasValue)
            {
                return true;
            }
            else if (this.HasValue && other.HasValue)
            {
                return EqualityComparer<T>.Default.Equals(this.Value, other.Value);
            }

            return false;
        }

        [Pure]
        public override bool Equals(object obj) => obj is Option<T> && this.Equals((Option<T>)obj);

        [Pure]
        public override int GetHashCode()
        {
            if (this.HasValue)
            {
                return this.Value == null ? 1 : this.Value.GetHashCode();
            }

            return 0;
        }

        [Pure]
        public override string ToString() =>
            this.Map(v => v != null ? string.Format(CultureInfo.InvariantCulture, "Some({0})", v) : "Some(null)").GetOrElse("None");

        [Pure]
        public IEnumerable<T> ToEnumerable()
        {
            if (this.HasValue)
            {
                yield return this.Value;
            }
        }

        [Pure]
        public IEnumerator<T> GetEnumerator()
        {
            if (this.HasValue)
            {
                yield return this.Value;
            }
        }

        [Pure]
        public bool Contains(T value)
        {
            if (this.HasValue)
            {
                return this.Value == null ? value == null : this.Value.Equals(value);
            }

            return false;
        }

        /// <summary>
        /// Evaluates to true if and only if the option has a value and <paramref name="predicate"/>
        /// returns <c>true</c>.
        /// </summary>
        [Pure]
        public bool Exists(Func<T, bool> predicate) => this.HasValue && predicate(this.Value);

        /// <summary>
        /// If this option has a value then returns that. If there is no value then returns
        /// <paramref name="alternative"/>.
        /// </summary>
        /// <param name="alternative"></param>
        /// <returns></returns>
        public T GetOrElse(T alternative) => this.HasValue ? this.Value : alternative;

        public T GetOrElse(Func<T> alternativeMaker) => this.HasValue ? this.Value : alternativeMaker();

        public Option<T> Else(Option<T> alternativeOption) => this.HasValue ? this : alternativeOption;

        public Option<T> Else(Func<Option<T>> alternativeMaker) => this.HasValue ? this : alternativeMaker();

        [Pure]
        public T OrDefault() => this.HasValue ? this.Value : default(T);

        public T Expect<TException>(Func<TException> exception)
            where TException : Exception
        {
            return this.HasValue
                ? this.Value
                : throw exception();
        }

        /// <summary>
        /// If the option has a value then it invokes <paramref name="some"/>. If there is no value
        /// then it invokes <paramref name="none"/>.
        /// </summary>
        /// <returns>The value returned by either <paramref name="some"/> or <paramref name="none"/>.</returns>
        [Pure]
        public TResult Match<TResult>(Func<T, TResult> some, Func<TResult> none) => this.HasValue ? some(this.Value) : none();

        /// <summary>
        /// Conditionally invokes <paramref name="action"/> with the value of this option
        /// object if this option has a value. This method is a no-op if there is no value
        /// stored in this option.
        /// </summary>
        public void ForEach(Action<T> action)
        {
            if (this.HasValue)
            {
                action(this.Value);
            }
        }

        public Task ForEachAsync(Func<T, Task> action) => this.HasValue ? action(this.Value) : Task.CompletedTask;

        /// <summary>
        /// If this option has a value then it transforms it into a new option instance by
        /// calling the <paramref name="mapping"/> callback.  It will follow exception if callback returns null.
        /// Returns <see cref="Option.None{T}"/> if there is no value.
        /// </summary>
        [Pure]
        public Option<TResult> Map<TResult>(Func<T, TResult> mapping)
        {
            return this.Match(
                some: value => Option.Some(mapping(value)),
                none: Option.None<TResult>);
        }

        [Pure]
        public Option<TResult> FlatMap<TResult>(Func<T, Option<TResult>> mapping) => this.Match(
            some: mapping,
            none: Option.None<TResult>);

        /// <summary>
        /// This method returns <c>this</c> if <paramref name="predicate"/> returns <c>true</c> and
        /// <c>Option.None&lt;T&gt;()</c> if it returns <c>false</c>. If the <c>Option&lt;T&gt;</c>
        /// does not have a value then it returns <c>this</c> instance as is.
        /// </summary>
        /// <param name="predicate">The callback function defining the filter condition.</param>
        /// <returns><c>this</c> if <paramref name="predicate"/> returns <c>true</c> and
        /// <c>Option.None&lt;T&gt;()</c> if it returns <c>false</c>. If the option has no
        /// value then it returns <c>this</c> instance as is.</returns>
        /// <remarks>
        /// Think of this like a standard C# "if" statement. For e.g., the following code:
        ///
        /// <code>
        /// Option&lt;string&gt; o = Option.Some("foo");
        /// o.Filter(s =&gt; s.Contains("foo")).ForEach(s =&gt; Console.WriteLine($"s = {s}"));
        /// </code>
        ///
        /// is semantically equivalent to:
        ///
        /// <code>
        /// string s = "foo";
        /// if (s != null &amp;&amp; s.Contains("foo"))
        /// {
        ///     Console.WriteLine($"s = {s}");
        /// }
        /// </code>
        /// </remarks>
        [Pure]
        public Option<T> Filter(Func<T, bool> predicate)
        {
            Option<T> original = this;
            return this.Match(
                some: value => predicate(value) ? original : Option.None<T>(),
                none: () => original);
        }
    }

    public static class Option
    {
        /// <summary>
        /// Creates an <c>Option &lt;T&gt;</c> with <paramref name="value"/> and marks
        /// the option object as having a value, i.e., <c>Option&lt;T&gt;.HasValue == true</c>.
        /// </summary>
        public static Option<T> Some<T>(T value)
        {
            Preconditions.CheckNotNull(value, nameof(value));

            return new Option<T>(value, true);
        }

        /// <summary>
        /// Creates an <c>Option &lt;T&gt;</c> with a default value (<c>default(T)</c>) and marks
        /// the option object as having no value, i.e., <c>Option&lt;T&gt;.HasValue == false</c>.
        /// </summary>
        public static Option<T> None<T>() => new Option<T>(default(T), false);

        public static Option<T> Maybe<T>(T value)
            where T : class => value == null ? None<T>() : Some(value);
    }

    public static class ShutdownHandler
    {
        /// <summary>
        /// Here are some references which were used for this code -
        /// https://stackoverflow.com/questions/40742192/how-to-do-gracefully-shutdown-on-dotnet-with-docker/43813871
        /// https://msdn.microsoft.com/en-us/library/system.gc.keepalive(v=vs.110).aspx
        /// </summary>
        public static (CancellationTokenSource cts, ManualResetEventSlim doneSignal, Option<object> handler)
            Init(TimeSpan shutdownWaitPeriod, ILogger logger)
        {
            var cts = new CancellationTokenSource();
            var completed = new ManualResetEventSlim();
            Option<object> handler = Option.None<object>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                WindowsShutdownHandler.HandlerRoutine hr = WindowsShutdownHandler.Init(cts, completed, shutdownWaitPeriod, logger);
                handler = Option.Some(hr as object);
            }
            else
            {
                LinuxShutdownHandler.Init(cts, completed, shutdownWaitPeriod, logger);
            }

            return (cts, completed, handler);
        }

        static class LinuxShutdownHandler
        {
            public static void Init(CancellationTokenSource cts, ManualResetEventSlim completed, TimeSpan shutdownWaitPeriod, ILogger logger)
            {
                void OnUnload(AssemblyLoadContext ctx) => CancelProgram();

                void CancelProgram()
                {
                    logger?.LogInformation("Termination requested, initiating shutdown.");
                    cts.Cancel();
                    logger?.LogInformation("Waiting for cleanup to finish");
                    // Wait for shutdown operations to complete.
                    if (completed.Wait(shutdownWaitPeriod))
                    {
                        logger?.LogInformation("Done with cleanup. Shutting down.");
                    }
                    else
                    {
                        logger?.LogInformation("Timed out waiting for cleanup to finish. Shutting down.");
                    }
                }

                AssemblyLoadContext.Default.Unloading += OnUnload;
                Console.CancelKeyPress += (sender, cpe) => CancelProgram();
                logger?.LogDebug("Waiting on shutdown handler to trigger");
            }
        }

        /// <summary>
        /// This is the recommended way to handle shutdown of windows containers. References -
        /// https://github.com/moby/moby/issues/25982
        /// https://gist.github.com/darstahl/fbb80c265dcfd1b327aabcc0f3554e56
        /// </summary>
        static class WindowsShutdownHandler
        {
            public delegate bool HandlerRoutine(CtrlTypes ctrlType);

            public enum CtrlTypes
            {
                CTRL_C_EVENT = 0,
                CTRL_BREAK_EVENT = 1,
                CTRL_CLOSE_EVENT = 2,
                CTRL_LOGOFF_EVENT = 5,
                CTRL_SHUTDOWN_EVENT = 6
            }

            public static HandlerRoutine Init(
                CancellationTokenSource cts,
                ManualResetEventSlim completed,
                TimeSpan waitPeriod,
                ILogger logger)
            {
                var hr = new HandlerRoutine(
                    type =>
                    {
                        logger?.LogInformation($"Received signal of type {type}");
                        if (type == CtrlTypes.CTRL_SHUTDOWN_EVENT)
                        {
                            logger?.LogInformation("Initiating shutdown");
                            cts.Cancel();
                            logger?.LogInformation("Waiting for cleanup to finish");
                            if (completed.Wait(waitPeriod))
                            {
                                logger?.LogInformation("Done with cleanup. Shutting down.");
                            }
                            else
                            {
                                logger?.LogInformation("Timed out waiting for cleanup to finish. Shutting down.");
                            }
                        }

                        return false;
                    });
                SetConsoleCtrlHandler(hr, true);
                logger?.LogDebug("Waiting on shutdown handler to trigger");
                return hr;
            }

            [DllImport("Kernel32")]
            static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);
        }
    }

    public static class TaskEx
    {
        public static Task Done { get; } = Task.FromResult(true);

        public static Task FromException(Exception exception) =>
            FromException<bool>(exception);

        public static Task<T> FromException<T>(Exception exception)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.TrySetException(exception);
            return tcs.Task;
        }

        public static Task WhenCanceled(this CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        public static async Task<(T1, T2)> WhenAll<T1, T2>(Task<T1> t1, Task<T2> t2)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            return (val1, val2);
        }

        public static async Task<(T1, T2, T3)> WhenAll<T1, T2, T3>(Task<T1> t1, Task<T2> t2, Task<T3> t3)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            return (val1, val2, val3);
        }

        public static async Task<(T1, T2, T3, T4)> WhenAll<T1, T2, T3, T4>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            return (val1, val2, val3, val4);
        }

        public static async Task<(T1, T2, T3, T4, T5)> WhenAll<T1, T2, T3, T4, T5>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            T5 val5 = await t5;
            return (val1, val2, val3, val4, val5);
        }

        public static async Task<(T1, T2, T3, T4, T5, T6)> WhenAll<T1, T2, T3, T4, T5, T6>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            T5 val5 = await t5;
            T6 val6 = await t6;
            return (val1, val2, val3, val4, val5, val6);
        }

        public static async Task<(T1, T2, T3, T4, T5, T6, T7)> WhenAll<T1, T2, T3, T4, T5, T6, T7>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6, Task<T7> t7)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            T5 val5 = await t5;
            T6 val6 = await t6;
            T7 val7 = await t7;
            return (val1, val2, val3, val4, val5, val6, val7);
        }

        public static async Task<(T1, T2, T3, T4, T5, T6, T7, T8)> WhenAll<T1, T2, T3, T4, T5, T6, T7, T8>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6, Task<T7> t7, Task<T8> t8)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            T5 val5 = await t5;
            T6 val6 = await t6;
            T7 val7 = await t7;
            T8 val8 = await t8;
            return (val1, val2, val3, val4, val5, val6, val7, val8);
        }

        public static async Task<(T1, T2, T3, T4, T5, T6, T7, T8, T9)> WhenAll<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6, Task<T7> t7, Task<T8> t8, Task<T9> t9)
        {
            T1 val1 = await t1;
            T2 val2 = await t2;
            T3 val3 = await t3;
            T4 val4 = await t4;
            T5 val5 = await t5;
            T6 val6 = await t6;
            T7 val7 = await t7;
            T8 val8 = await t8;
            T9 val9 = await t9;
            return (val1, val2, val3, val4, val5, val6, val7, val8, val9);
        }

        public static async Task<T> TimeoutAfter<T>(this Task<T> task, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                Task timerTask = Task.Delay(timeout, cts.Token);
                Task completedTask = await Task.WhenAny(task, timerTask);
                if (completedTask == timerTask)
                {
                    throw new TimeoutException("Operation timed out");
                }

                cts.Cancel();
                return await task;
            }
        }

        public static async Task TimeoutAfter(this Task task, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                Task timerTask = Task.Delay(timeout, cts.Token);
                Task completedTask = await Task.WhenAny(task, timerTask);
                if (completedTask == timerTask)
                {
                    throw new TimeoutException("Operation timed out");
                }

                cts.Cancel();
                await task;
            }
        }

        public static Task TimeoutAfter(this Func<CancellationToken, Task> operation, CancellationToken cancellationToken, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                try
                {
                    return operation(CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token)
                        .TimeoutAfter(timeout);
                }
                catch (TimeoutException)
                {
                    cts.Cancel();
                    throw;
                }
            }
        }

        public static Task<T> TimeoutAfter<T>(this Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                try
                {
                    return operation(CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token)
                        .TimeoutAfter(timeout);
                }
                catch (TimeoutException)
                {
                    cts.Cancel();
                    throw;
                }
            }
        }

        public static Task<T> ExecuteUntilCancelled<T>(this Func<T> operation, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(operation, nameof(operation));
            Task<T> task = Task.Run(operation, cancellationToken);
            return task.ExecuteUntilCancelled(cancellationToken);
        }

        public static Task ExecuteUntilCancelled(this Action operation, CancellationToken cancellationToken)
        {
            Preconditions.CheckNotNull(operation, nameof(operation));
            Task task = Task.Run(operation, cancellationToken);
            return task.ExecuteUntilCancelled(cancellationToken);
        }

        public static IAsyncResult ToAsyncResult(this Task task, AsyncCallback callback, object state)
        {
            if (task.AsyncState == state)
            {
                if (callback != null)
                {
                    task.ContinueWith(
                        (t, st) => ((AsyncCallback)state)(t),
                        callback,
                        TaskContinuationOptions.ExecuteSynchronously);
                }

                return task;
            }

            var tcs = new TaskCompletionSource<object>(state);
            task.ContinueWith(
                t =>
                {
                    switch (t.Status)
                    {
                        case TaskStatus.RanToCompletion:
                            tcs.TrySetResult(null);
                            break;
                        case TaskStatus.Canceled:
                            tcs.TrySetCanceled();
                            break;
                        case TaskStatus.Faulted:
                            if (t.Exception != null)
                                tcs.TrySetException(t.Exception.InnerExceptions);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    callback?.Invoke(tcs.Task);
                },
                TaskContinuationOptions.ExecuteSynchronously);

            return tcs.Task;
        }

        public static void EndAsyncResult(IAsyncResult asyncResult)
        {
            if (!(asyncResult is Task task))
            {
                throw new ArgumentException("IAsyncResult should be of type Task");
            }

            try
            {
                task.Wait();
            }
            catch (AggregateException ae)
            {
                throw ae.GetBaseException();
            }
        }

        static async Task<T> ExecuteUntilCancelled<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<T>();
            cancellationToken.Register(
                () => { tcs.SetException(new TaskCanceledException(task)); });
            Task<T> completedTask = await Task.WhenAny(task, tcs.Task);
            return await completedTask;
        }

        static async Task ExecuteUntilCancelled(this Task task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<int>();
            cancellationToken.Register(
                () => { tcs.TrySetCanceled(); });
            Task completedTask = await Task.WhenAny(task, tcs.Task);
            //// Await here to bubble up any exceptions
            await completedTask;
        }
    }

    public class Preconditions
    {
        /// <summary>
        /// Checks that a reference isn't null. Throws ArgumentNullException if null.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="reference"></param>
        /// <returns>The reference</returns>
        public static T CheckNotNull<T>(T reference) => CheckNotNull(reference, string.Empty, string.Empty);

        /// <summary>
        /// Checks that a reference isn't null. Throws ArgumentNullException if null.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="reference"></param>
        /// <param name="paramName"></param>
        /// <returns>The reference</returns>
        public static T CheckNotNull<T>(T reference, string paramName) => CheckNotNull(reference, paramName, string.Empty);

        /// <summary>
        /// Checks that a reference isn't null. Throws ArgumentNullException if null.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="reference"></param>
        /// <param name="paramName"></param>
        /// <param name="message"></param>
        /// <returns>The reference</returns>
        public static T CheckNotNull<T>(T reference, string paramName, string message)
        {
            if (reference == null)
            {
                if (string.IsNullOrEmpty(paramName))
                {
                    throw new ArgumentNullException();
                }
                else
                {
                    throw string.IsNullOrEmpty(message) ? new ArgumentNullException(paramName) : new ArgumentNullException(paramName, message);
                }
            }

            return reference;
        }

        /// <summary>
        /// Throws ArgumentException if the bool expression is false.
        /// </summary>
        /// <param name="expression"></param>
        public static void CheckArgument(bool expression)
        {
            if (!expression)
            {
                throw new ArgumentException();
            }
        }

        /// <summary>
        /// Throws ArgumentException if the bool expression is false.
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="message"></param>
        public static void CheckArgument(bool expression, string message)
        {
            if (!expression)
            {
                throw new ArgumentException(message);
            }
        }

        /// <summary>
        ///  Checks that an Enum is defined. Throws ArgumentOutOfRangeException is not.
        /// </summary>
        /// <typeparam name="T">Enum Type.</typeparam>
        /// <param name="status">Value.</param>
        /// <returns></returns>
        public static T CheckIsDefined<T>(T status)
        {
            Type enumType = typeof(T);
            if (!Enum.IsDefined(enumType, status))
            {
                throw new ArgumentOutOfRangeException(status + " is not a valid value for " + enumType.FullName + ".");
            }

            return status;
        }

        /// <summary>
        /// This checks that the item is greater than or equal to the low value.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low)
            where T : IComparable<T> =>
            CheckRange(item, low, nameof(item));

        /// <summary>
        /// This checks that the item is greater than or equal to the low value.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low, string paramName)
            where T : IComparable<T> =>
            CheckRange(item, low, paramName, string.Empty);

        /// <summary>
        /// This checks that the item is greater than or equal to the low value.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <param name="paramName"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low, string paramName, string message)
            where T : IComparable<T>
        {
            if (item.CompareTo(low) < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, item, message);
            }

            return item;
        }

        /// <summary>
        /// This checks that the item is in the range [low, high).
        /// Throws ArgumentOutOfRangeException if out of range.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <param name="high">Exclusive high value</param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low, T high)
            where T : IComparable<T> =>
            CheckRange(item, low, high, nameof(item));

        /// <summary>
        /// This checks that the item is in the range [low, high).
        /// Throws ArgumentOutOfRangeException if out of range.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <param name="high">Exclusive high value</param>
        /// <param name="paramName"></param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low, T high, string paramName)
            where T : IComparable<T> =>
            CheckRange(item, low, high, paramName, string.Empty);

        /// <summary>
        /// This checks that the item is in the range [low, high).
        /// Throws ArgumentOutOfRangeException if out of range.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">Item to check.</param>
        /// <param name="low">Inclusive low value.</param>
        /// <param name="high">Exclusive high value</param>
        /// <param name="paramName"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public static T CheckRange<T>(T item, T low, T high, string paramName, string message)
            where T : IComparable<T>
        {
            if (item.CompareTo(low) < 0 || item.CompareTo(high) >= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, item, message);
            }

            return item;
        }

        /// <summary>
        /// Checks if the string is null or whitespace, and throws ArgumentException if it is.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="paramName"></param>
        public static string CheckNonWhiteSpace(string value, string paramName)
        {
            CheckArgument(!string.IsNullOrWhiteSpace(value), $"{paramName} is null or whitespace.");
            return value;
        }
    }

    /// <summary>
    /// Represents a retry strategy with a specified number of retry attempts and a default, fixed time interval between retries.
    /// </summary>
    public class FixedInterval : RetryStrategy
    {
        readonly int retryCount;

        readonly TimeSpan retryInterval;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> class.
        /// </summary>
        public FixedInterval()
            : this(DefaultClientRetryCount)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> class with the specified number of retry attempts.
        /// </summary>
        /// <param name="retryCount">The number of retry attempts.</param>
        public FixedInterval(int retryCount)
            : this(retryCount, DefaultRetryInterval)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> class with the specified number of retry attempts, time interval, and retry strategy.
        /// </summary>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="retryInterval">The time interval between retries.</param>
        public FixedInterval(int retryCount, TimeSpan retryInterval)
            : this(retryCount, retryInterval, DefaultFirstFastRetry)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.FixedInterval" /> class with the specified number of retry attempts, time interval, retry strategy, and fast start option.
        /// </summary>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="retryInterval">The time interval between retries.</param>
        /// <param name="firstFastRetry">true to immediately retry in the first attempt; otherwise, false. The subsequent retries will remain subject to the configured retry interval.</param>
        public FixedInterval(int retryCount, TimeSpan retryInterval, bool firstFastRetry)
            : base(firstFastRetry)
        {
            Guard.ArgumentNotNegativeValue(retryCount, "retryCount");
            Guard.ArgumentNotNegativeValue(retryInterval.Ticks, "retryInterval");
            this.retryCount = retryCount;
            this.retryInterval = retryInterval;
        }

        /// <summary>
        /// Returns the corresponding ShouldRetry delegate.
        /// </summary>
        /// <returns>The ShouldRetry delegate.</returns>
        public override ShouldRetry GetShouldRetry()
        {
            if (this.retryCount == 0)
            {
                return (int currentRetryCount, Exception lastException, out TimeSpan interval) =>
                {
                    interval = TimeSpan.Zero;
                    return false;
                };
            }

            return (int currentRetryCount, Exception lastException, out TimeSpan interval) =>
            {
                if (currentRetryCount < this.retryCount)
                {
                    interval = this.retryInterval;
                    return true;
                }

                interval = TimeSpan.Zero;
                return false;
            };
        }
    }

    //
    // Summary:
    //     Indicates that a type or method is pure, that is, it does not make any visible
    //     state changes.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Parameter | AttributeTargets.Delegate, AllowMultiple = false, Inherited = true)]
    [Conditional("CONTRACTS_FULL")]
    public sealed class PureAttribute : Attribute
    {
        //
        // Summary:
        //     Initializes a new instance of the System.Diagnostics.Contracts.PureAttribute
        //     class.
        public PureAttribute() { }
    }

    /// <summary>
    /// A retry strategy with a specified number of retry attempts and an incremental time interval between retries.
    /// </summary>
    class Incremental : RetryStrategy
    {
        readonly int retryCount;
        readonly TimeSpan initialInterval;
        readonly TimeSpan increment;

        /// <summary>
        /// Initializes a new instance of the <see cref="Incremental"/> class.
        /// </summary>
        public Incremental()
            : this(DefaultClientRetryCount, DefaultRetryInterval, DefaultRetryIncrement)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Incremental"/> class with the specified name and retry settings.
        /// </summary>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="initialInterval">The initial interval that will apply for the first retry.</param>
        /// <param name="increment">The incremental time value that will be used to calculate the progressive delay between retries.</param>
        public Incremental(int retryCount, TimeSpan initialInterval, TimeSpan increment)
            : this(retryCount, initialInterval, increment, DefaultFirstFastRetry)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Incremental"/> class with the specified number of retry attempts, time interval, retry strategy, and fast start option.
        /// </summary>
        /// <param name="retryCount">The number of retry attempts.</param>
        /// <param name="initialInterval">The initial interval that will apply for the first retry.</param>
        /// <param name="increment">The incremental time value that will be used to calculate the progressive delay between retries.</param>
        /// <param name="firstFastRetry">true to immediately retry in the first attempt; otherwise, false. The subsequent retries will remain subject to the configured retry interval.</param>
        public Incremental(int retryCount, TimeSpan initialInterval, TimeSpan increment, bool firstFastRetry)
            : base(firstFastRetry)
        {
            Guard.ArgumentNotNegativeValue(retryCount, "retryCount");
            Guard.ArgumentNotNegativeValue(initialInterval.Ticks, "initialInterval");
            Guard.ArgumentNotNegativeValue(increment.Ticks, "increment");
            this.retryCount = retryCount;
            this.initialInterval = initialInterval;
            this.increment = increment;
        }

        /// <summary>
        /// Returns the corresponding ShouldRetry delegate.
        /// </summary>
        /// <returns>The ShouldRetry delegate.</returns>
        public override ShouldRetry GetShouldRetry()
        {
            return (int currentRetryCount, Exception lastException, out TimeSpan retryInterval) =>
            {
                if (currentRetryCount < this.retryCount)
                {
                    retryInterval = TimeSpan.FromMilliseconds(this.initialInterval.TotalMilliseconds + this.increment.TotalMilliseconds * currentRetryCount);
                    return true;
                }

                retryInterval = TimeSpan.Zero;
                return false;
            };
        }
    }

    /// <summary>
    /// Contains information that is required for the <see cref="E:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryPolicy.Retrying" /> event.
    /// </summary>
    public class RetryingEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.RetryingEventArgs" /> class.
        /// </summary>
        /// <param name="currentRetryCount">The current retry attempt count.</param>
        /// <param name="lastException">The exception that caused the retry conditions to occur.</param>
        public RetryingEventArgs(int currentRetryCount, Exception lastException)
        {
            Guard.ArgumentNotNull(lastException, "lastException");
            this.CurrentRetryCount = currentRetryCount;
            this.LastException = lastException;
        }

        /// <summary>
        /// Gets the current retry count.
        /// </summary>
        public int CurrentRetryCount { get; set; }

        /// <summary>
        /// Gets the exception that caused the retry conditions to occur.
        /// </summary>
        public Exception LastException { get; set; }
    }

    /// <summary>
    /// Provides a wrapper for a non-generic <see cref="T:System.Threading.Tasks.Task" /> and calls into the pipeline
    /// to retry only the generic version of the <see cref="T:System.Threading.Tasks.Task" />.
    /// </summary>
    class AsyncExecution : AsyncExecution<bool>
    {
        static Task<bool> cachedBoolTask;

        public AsyncExecution(Func<Task> taskAction, ShouldRetry shouldRetry, Func<Exception, bool> isTransient, Action<int, Exception, TimeSpan> onRetrying, bool fastFirstRetry, CancellationToken cancellationToken)
            : base(() => StartAsGenericTask(taskAction), shouldRetry, isTransient, onRetrying, fastFirstRetry, cancellationToken)
        {
        }

        /// <summary>
        /// Wraps the non-generic <see cref="T:System.Threading.Tasks.Task" /> into a generic <see cref="T:System.Threading.Tasks.Task" />.
        /// </summary>
        /// <param name="taskAction">The task to wrap.</param>
        /// <returns>A <see cref="T:System.Threading.Tasks.Task" /> that wraps the non-generic <see cref="T:System.Threading.Tasks.Task" />.</returns>
        static Task<bool> StartAsGenericTask(Func<Task> taskAction)
        {
            Task task = taskAction();
            if (task == null)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} cannot be null",
                        new object[]
                        {
                            "taskAction"
                        }),
                    nameof(taskAction));
            }

            if (task.Status == TaskStatus.RanToCompletion)
            {
                return GetCachedTask();
            }

            if (task.Status == TaskStatus.Created)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} must be scheduled",
                        new object[]
                        {
                            "taskAction"
                        }),
                    nameof(taskAction));
            }

            var tcs = new TaskCompletionSource<bool>();
            task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        if (t.Exception != null)
                            tcs.TrySetException(t.Exception.InnerExceptions);
                        return;
                    }

                    if (t.IsCanceled)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    tcs.TrySetResult(true);
                },
                TaskContinuationOptions.ExecuteSynchronously);
            return tcs.Task;
        }

        static Task<bool> GetCachedTask()
        {
            if (cachedBoolTask == null)
            {
                var taskCompletionSource = new TaskCompletionSource<bool>();
                taskCompletionSource.TrySetResult(true);
                cachedBoolTask = taskCompletionSource.Task;
            }

            return cachedBoolTask;
        }
    }

    /// <summary>
    /// The special type of exception that provides managed exit from a retry loop. The user code can use this
    /// exception to notify the retry policy that no further retry attempts are required.
    /// </summary>
    [Obsolete("You should use cancellation tokens or other means of stoping the retry loop.")]
    sealed class RetryLimitExceededException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.WebJobs.Extensions.EdgeHub.RetryLimitExceededException" /> class with a default error message.
        /// </summary>
        public RetryLimitExceededException()
            : this("Retry limit exceeded")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.WebJobs.Extensions.EdgeHub.RetryLimitExceededException" /> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public RetryLimitExceededException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.WebJobs.Extensions.EdgeHub.RetryLimitExceededException" /> class with a reference to the inner exception
        /// that is the cause of this exception.
        /// </summary>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public RetryLimitExceededException(Exception innerException)
            : base((innerException != null) ? innerException.Message : "Retry limit exceeded", innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Microsoft.Azure.WebJobs.Extensions.EdgeHub.RetryLimitExceededException" /> class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public RetryLimitExceededException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Handles the execution and retries of the user-initiated task.
    /// </summary>
    /// <typeparam name="TResult">The result type of the user-initiated task.</typeparam>
    class AsyncExecution<TResult>
    {
        readonly Func<Task<TResult>> taskFunc;

        readonly ShouldRetry shouldRetry;

        readonly Func<Exception, bool> isTransient;

        readonly Action<int, Exception, TimeSpan> onRetrying;

        readonly bool fastFirstRetry;

        readonly CancellationToken cancellationToken;

        Task<TResult> previousTask;

        int retryCount;

        public AsyncExecution(Func<Task<TResult>> taskFunc, ShouldRetry shouldRetry, Func<Exception, bool> isTransient, Action<int, Exception, TimeSpan> onRetrying, bool fastFirstRetry, CancellationToken cancellationToken)
        {
            this.taskFunc = taskFunc;
            this.shouldRetry = shouldRetry;
            this.isTransient = isTransient;
            this.onRetrying = onRetrying;
            this.fastFirstRetry = fastFirstRetry;
            this.cancellationToken = cancellationToken;
        }

        internal Task<TResult> ExecuteAsync()
        {
            return this.ExecuteAsyncImpl(null);
        }

        Task<TResult> ExecuteAsyncImpl(Task ignore)
        {
            if (this.cancellationToken.IsCancellationRequested)
            {
                if (this.previousTask != null)
                {
                    return this.previousTask;
                }

                var taskCompletionSource = new TaskCompletionSource<TResult>();
                taskCompletionSource.TrySetCanceled();
                return taskCompletionSource.Task;
            }
            else
            {
                Task<TResult> task;
                try
                {
                    task = this.taskFunc();
                }
                catch (Exception ex)
                {
                    if (!this.isTransient(ex))
                    {
                        throw;
                    }

                    var taskCompletionSource2 = new TaskCompletionSource<TResult>();
                    taskCompletionSource2.TrySetException(ex);
                    task = taskCompletionSource2.Task;
                }

                if (task == null)
                {
                    throw new ArgumentException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} cannot be null",
                            new object[]
                            {
                                "taskFunc"
                            }),
                        nameof(this.taskFunc));
                }

                if (task.Status == TaskStatus.RanToCompletion)
                {
                    return task;
                }

                if (task.Status == TaskStatus.Created)
                {
                    throw new ArgumentException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} must be scheduled",
                            new object[]
                            {
                                "taskFunc"
                            }),
                        nameof(this.taskFunc));
                }

                return task.ContinueWith(new Func<Task<TResult>, Task<TResult>>(this.ExecuteAsyncContinueWith), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            }
        }

        Task<TResult> ExecuteAsyncContinueWith(Task<TResult> runningTask)
        {
            if (!runningTask.IsFaulted || this.cancellationToken.IsCancellationRequested)
            {
                return runningTask;
            }

            TimeSpan zero;
            Exception innerException = runningTask.Exception.InnerException;
#pragma warning disable CS0618 // Type or member is obsolete
            if (innerException is RetryLimitExceededException)
#pragma warning restore CS0618 // Type or member is obsolete
            {
                var taskCompletionSource = new TaskCompletionSource<TResult>();
                if (innerException.InnerException != null)
                {
                    taskCompletionSource.TrySetException(innerException.InnerException);
                }
                else
                {
                    taskCompletionSource.TrySetCanceled();
                }

                return taskCompletionSource.Task;
            }

            if (!this.isTransient(innerException) || !this.shouldRetry(this.retryCount++, innerException, out zero))
            {
                return runningTask;
            }

            if (zero < TimeSpan.Zero)
            {
                zero = TimeSpan.Zero;
            }

            this.onRetrying(this.retryCount, innerException, zero);
            this.previousTask = runningTask;
            if (zero > TimeSpan.Zero && (this.retryCount > 1 || !this.fastFirstRetry))
            {
                return Task.Delay(zero, this.cancellationToken).ContinueWith(new Func<Task, Task<TResult>>(this.ExecuteAsyncImpl), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            }

            return this.ExecuteAsyncImpl(null);
        }
    }

    public static class ExceptionEx
    {
        public static bool IsFatal(this Exception exception)
        {
            while (exception != null)
            {
                switch (exception)
                {
                    // ReSharper disable once UnusedVariable
                    case OutOfMemoryException ex:
                        return true;
                    // ReSharper disable once UnusedVariable
                    case SEHException ex:
                        return true;
                }

                // These exceptions aren't themselves fatal, but since the CLR uses them to wrap other exceptions,
                // we want to check to see whether they've been used to wrap a fatal exception.  If so, then they
                // count as fatal.
                if (exception is TypeInitializationException || exception is TargetInvocationException)
                {
                    exception = exception.InnerException;
                }
                else if (exception is AggregateException)
                {
                    // AggregateExceptions have a collection of inner exceptions, which may themselves be other
                    // wrapping exceptions (including nested AggregateExceptions).  Recursively walk this
                    // hierarchy.  The (singular) InnerException is included in the collection.
                    ReadOnlyCollection<Exception> innerExceptions = ((AggregateException)exception).InnerExceptions;
                    if (innerExceptions.Any(ex => IsFatal(ex)))
                    {
                        return true;
                    }

                    break;
                }
                else if (exception is NullReferenceException)
                {
                    break;
                }
                else
                {
                    break;
                }
            }

            return false;
        }

        public static T UnwindAs<T>(this Exception exception)
            where T : Exception
        {
            switch (exception)
            {
                case T tException:
                    return tException;
                case AggregateException aggregateException when aggregateException.InnerExceptions.Count == 1:
                    return UnwindAs<T>(aggregateException.InnerException);
                default:
                    return null;
            }
        }

        public static bool HasTimeoutException(this Exception ex) =>
            ex != null &&
            (ex is TimeoutException || HasTimeoutException(ex.InnerException) ||
             (ex is AggregateException argEx && (argEx.InnerExceptions?.Select(e => HasTimeoutException(e)).Any(e => e) ?? false)));
    }

    public class AirHumidity : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float MaxAirTemp1;
        public float CurAirTemp1;
        public float MinAirTemp1;
        public float AirTempQ;
        public float AirTemp2;
        public float AirTemp2Q;
        public float Rh;
        public float DewPoint;
    }

    public class AtmosPressure : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float AtmPressure;
    }

    public class Pavement : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float PvmntTemp1;
        public float PavementQ;
        public float AltPaveTemp1;
        public float FrzPntTemp1;
        public float FrzPntTemp1Q;
        public float PvmnCond1;
        public float PvmntCond1Q;
        public float SbAsphltTemp;
        public float PvBaseTemp1;
        public float PvBaseTemp1Q;
        public float PvmntSrfCvTh;
        public float PvmntSrfCvThQ;
    }

    public class Precipitation : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float GaugeTot;
        public float NewPrecip;
        public float HrlyPrecip;
        public float PrecipGaugeQ;
        public float PrecipDetRatio;
        public float PrecipDetQ;
    }

    public class Snow : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float HS;
        public float HStd;
        public float HrlySnow;
        public float SnowQ;
    }

    public class Wind : StationData
    {
        public string TmStamp;
        public string RecNum;
        public int StationId;
        public float Identifier;
        public float MaxWindSpd;
        public float MeanWindSpd;
        public float WindSpd;
        public float WindSpdQ;
        public float MeanWindDir;
        public float StDevWind;
        public float WindDir;
        public float DerimeStat;
    }


    public class AirHumidities : StationDatas
    {
        public string deviceId;
        public AirHumidityMeasurements measurements;

    }

    public class AtmosPressures : StationDatas
    {
        public string deviceId;
        public AtmosPressureMeasurements measurements;

    }

    public class Pavements : StationDatas
    {
        public string deviceId;
        public PavementMeasurements measurements;

    }

    public class Precipitations : StationDatas
    {
        public string deviceId;
        public PrecipitationMeasurements measurements;

    }

    public class Snows : StationDatas
    {
        public string deviceId;
        public SnowMeasurements measurements;

    }

    public class Winds : StationDatas
    {
        public string deviceId;
        public WindMeasurements measurements;

    }


    public class MessageBody
    {
        [JsonProperty(PropertyName = "deviceId")]
        public string DeviceId { get; set; }




    }

    public class AirHumidityMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public AirHumidityMeasurements Measurements { get; set; }

    }

    public class AtmosPressureMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public AtmosPressureMeasurements Measurements { get; set; }

    }

    public class PavementMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public PavementMeasurements Measurements { get; set; }

    }

    public class PrecipitationMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public PrecipitationMeasurements Measurements { get; set; }

    }

    public class SnowMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public SnowMeasurements Measurements { get; set; }

    }

    public class WindMessageBody : MessageBody
    {
        [JsonProperty(PropertyName = "measurements")]
        public WindMeasurements Measurements { get; set; }

    }



    public class Device
    {
        [JsonProperty(PropertyName = "deviceId")]
        public string deviceId;

    }

    public class AirHumidityMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "maxAirTemp1")]
        public List<float> MaxAirTemp1 = new List<float>();
        [JsonProperty(PropertyName = "curAirTemp1")]
        public List<float> CurAirTemp1 = new List<float>();
        [JsonProperty(PropertyName = "minAirTemp1")]
        public List<float> MinAirTemp1 = new List<float>();
        [JsonProperty(PropertyName = "airTempQ")]
        public List<float> AirTempQ = new List<float>();
        [JsonProperty(PropertyName = "airTemp2")]
        public List<float> AirTemp2 = new List<float>();
        [JsonProperty(PropertyName = "airTemp2Q")]
        public List<float> AirTemp2Q = new List<float>();
        [JsonProperty(PropertyName = "rh")]
        public List<float> RH = new List<float>();
        [JsonProperty(PropertyName = "dewPoint")]
        public List<float> Dew_Point = new List<float>();
    }

    public class AtmosPressureMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "atmPressure")]
        public List<float> AtmPressure = new List<float>();

    }

    public class PavementMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "pvmntTemp1")]
        public List<float> PvmntTemp1 = new List<float>();
        [JsonProperty(PropertyName = "pavementQ")]
        public List<float> PavementQ = new List<float>();
        [JsonProperty(PropertyName = "altPaveTemp1")]
        public List<float> AltPaveTemp1 = new List<float>();
        [JsonProperty(PropertyName = "frzPntTemp1")]
        public List<float> FrzPntTemp1 = new List<float>();
        [JsonProperty(PropertyName = "frzPntTemp1Q")]
        public List<float> FrzPntTemp1Q = new List<float>();
        [JsonProperty(PropertyName = "pvmnCond1")]
        public List<float> PvmnCond1 = new List<float>();
        [JsonProperty(PropertyName = "pvmntCond1Q")]
        public List<float> PvmntCond1Q = new List<float>();
        [JsonProperty(PropertyName = "sbAsphltTemp")]
        public List<float> SbAsphltTemp = new List<float>();
        [JsonProperty(PropertyName = "pvBaseTemp1")]
        public List<float> PvBaseTemp1 = new List<float>();
        [JsonProperty(PropertyName = "pvBaseTemp1Q")]
        public List<float> PvBaseTemp1Q = new List<float>();
        [JsonProperty(PropertyName = "pvmntSrfCvTh")]
        public List<float> PvmntSrfCvTh = new List<float>();
        [JsonProperty(PropertyName = "pvmntSrfCvThQ")]
        public List<float> PvmntSrfCvThQ = new List<float>();
    }

    public class PrecipitationMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "gaugeTot")]
        public List<float> GaugeTot = new List<float>();
        [JsonProperty(PropertyName = "newPrecip")]
        public List<float> NewPrecip = new List<float>();
        [JsonProperty(PropertyName = "hrlyPrecip")]
        public List<float> HrlyPrecip = new List<float>();
        [JsonProperty(PropertyName = "precipGaugeQ")]
        public List<float> PrecipGaugeQ = new List<float>();
        [JsonProperty(PropertyName = "precipDetRatio")]
        public List<float> PrecipDetRatio = new List<float>();
        [JsonProperty(PropertyName = "precipDetQ")]
        public List<float> PrecipDetQ = new List<float>();

    }

    public class SnowMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "hS")]
        public List<float> HS = new List<float>();
        [JsonProperty(PropertyName = "hStd")]
        public List<float> HStd = new List<float>();
        [JsonProperty(PropertyName = "hrlySnow")]
        public List<float> HrlySnow = new List<float>();
        [JsonProperty(PropertyName = "nnowQ")]
        public List<float> SnowQ = new List<float>();

    }

    public class WindMeasurements
    {
        [JsonProperty(PropertyName = "tmStamp")]
        public List<string> TmStamp = new List<string>();
        [JsonProperty(PropertyName = "recNum")]
        public List<string> RecNum = new List<string>();
        [JsonProperty(PropertyName = "stationId")]
        public List<int> StationID = new List<int>();
        [JsonProperty(PropertyName = "identifier")]
        public List<float> Identifier = new List<float>();
        [JsonProperty(PropertyName = "maxWindSpd")]
        public List<float> MaxWindSpd = new List<float>();
        [JsonProperty(PropertyName = "meanWindSpd")]
        public List<float> MeanWindSpd = new List<float>();
        [JsonProperty(PropertyName = "windSpd")]
        public List<float> WindSpd = new List<float>();
        [JsonProperty(PropertyName = "windSpdQ")]
        public List<float> WindSpdQ = new List<float>();
        [JsonProperty(PropertyName = "meanWindDir")]
        public List<float> MeanWindDir = new List<float>();
        [JsonProperty(PropertyName = "stDevWind")]
        public List<float> StDevWind = new List<float>();
        [JsonProperty(PropertyName = "windDir")]
        public List<float> WindDir = new List<float>();
        [JsonProperty(PropertyName = "derimeStat")]
        public List<float> DerimeStat = new List<float>();
    }

    public class Measurements
    {
        [JsonProperty(PropertyName = "timestamp")]
        public string timestamp;

        [JsonProperty(PropertyName = "maxAirTemp")]
        public float maxAirTemp;

        [JsonProperty(PropertyName = "currentAirTemp")]
        public float currentAirTemp;

        [JsonProperty(PropertyName = "minAirTemp")]
        public float minAirTemp;

        [JsonProperty(PropertyName = "airTempQuality")]
        public float airTempQuality;


        [JsonProperty(PropertyName = "airTempAlternate")]
        public float airTempAlternate;



        [JsonProperty(PropertyName = "airTempAlternateQuality")]
        public float airTempAlternateQuality;


        [JsonProperty(PropertyName = "relativeHumidity")]
        public float relativeHumidity;


        [JsonProperty(PropertyName = "dewPoint")]
        public float dewPoint;



        [JsonProperty(PropertyName = "atmospherePressure")]
        public float atmospherePressure;


        [JsonProperty(PropertyName = "pavementTemp")]
        public float pavementTemp;




        [JsonProperty(PropertyName = "pavementTempQuality")]
        public float pavementTempQuality;



        [JsonProperty(PropertyName = "alternatePavementTemp")]
        public float alternatePavementTemp;


        [JsonProperty(PropertyName = "freezePointTemp")]
        public float freezePointTemp;



        [JsonProperty(PropertyName = "freezePointTempQuality")]
        public float freezePointTempQuality;


        [JsonProperty(PropertyName = "pavementCondition")]
        public float pavementCondition;


        [JsonProperty(PropertyName = "pavementConditionQuality")]
        public float pavementConditionQuality;


        [JsonProperty(PropertyName = "subAsphaltTemp")]
        public float subAsphaltTemp;


        [JsonProperty(PropertyName = "pavementBaseTemp")]
        public float pavementBaseTemp;


        [JsonProperty(PropertyName = "pavementBaseTempQuality")]
        public float pavementBaseTempQuality;


        [JsonProperty(PropertyName = "pavementSurfaceConductivity")]
        public float pavementSurfaceConductivity;


        [JsonProperty(PropertyName = "pavementSurfaceConductivityQuality")]
        public float pavementSurfaceConductivityQuality;


        [JsonProperty(PropertyName = "maxWindSpeed")]
        public float maxWindSpeed;

        [JsonProperty(PropertyName = "meanWindSpeed")]
        public float meanWindSpeed;


        [JsonProperty(PropertyName = "windSpeed")]
        public float windSpeed;


        [JsonProperty(PropertyName = "windSpeedQuality")]
        public float windSpeedQuality;


        [JsonProperty(PropertyName = "meanWindDirection")]
        public float meanWindDirection;

        [JsonProperty(PropertyName = "standardWindDeviation")]
        public float standardWindDeviation;

        [JsonProperty(PropertyName = "windDirection")]
        public float windDirection;



    }


    public interface StationData { }

    public interface StationDatas { }

    public class WeatherData
    {
        public StationDatas airHumidity;
        public StationDatas atmosPressure;
        public StationDatas pavement;
        public StationDatas precipitation;
        public StationDatas snow;
        public StationDatas wind;

        public string deviceId;
        public Measurements measurements;
    }

    public class WeatherData1
    {
        public string deviceId;
        public Measurements measurements;
    }
}