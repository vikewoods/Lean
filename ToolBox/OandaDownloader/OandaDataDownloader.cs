﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.ToolBox.OandaDownloader.OandaRestLibrary;

namespace QuantConnect.ToolBox.OandaDownloader
{
    /// <summary>
    /// Oanda Data Downloader class
    /// </summary>
    public class OandaDataDownloader : IDataDownloader
    {
        private const string InstrumentsFileName = "instruments_oanda.txt";
        private const int BarsPerRequest = 5000;

        private static Dictionary<string, LeanInstrument> _instruments;

        /// <summary>
        /// Initializes a new instance of the <see cref="OandaDataDownloader"/> class
        /// </summary>
        public OandaDataDownloader(string accessToken, int accountId)
        {
            LoadInstruments();

            // Set Oanda account credentials
            Credentials.SetCredentials(EEnvironment.Practice, accessToken, accountId);
        }

        /// <summary>
        /// Loads the instrument list from the instruments.txt file
        /// </summary>
        /// <returns></returns>
        private static void LoadInstruments()
        {
            if (!File.Exists(InstrumentsFileName))
                throw new FileNotFoundException(InstrumentsFileName + " file not found.");

            _instruments = new Dictionary<string, LeanInstrument>();

            var lines = File.ReadAllLines(InstrumentsFileName);
            foreach (var line in lines)
            {
                var tokens = line.Split(',');
                if (tokens.Length >= 3)
                {
                    var oandaSymbol = tokens[0];
                    var securityType = (SecurityType)Enum.Parse(typeof(SecurityType), tokens[2]);
                    var symbol = ConvertOandaSymbolToLeanSymbol(oandaSymbol);
                    _instruments.Add(symbol, new LeanInstrument
                    {
                        Symbol = symbol,
                        Name = tokens[1],
                        Type = securityType
                    });
                }
            }
        }

        /// <summary>
        /// Converts an Oanda symbol to a Lean Symbol instance
        /// </summary>
        /// <param name="oandaSymbol">The Oanda symbol</param>
        /// <returns>A Lean symbol</returns>
        private static string ConvertOandaSymbolToLeanSymbol(string oandaSymbol)
        {
            return oandaSymbol.Replace("_", "");
        }

        /// <summary>
        /// Converts a Lean symbol to an Oanda symbol
        /// </summary>
        /// <param name="symbol">The Lean symbol</param>
        /// <returns>An Oanda symbol</returns>
        private static string ConvertLeanSymbolToOandaSymbol(Symbol symbol)
        {
            // this will only work for forex symbols
            return symbol.Value.Insert(symbol.Value.Length - 3, "_");
        }

        /// <summary>
        /// Checks if downloader can get the data for the symbol
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns>Returns true if the symbol is available</returns>
        public bool HasSymbol(string symbol)
        {
            return _instruments.ContainsKey(symbol);
        }

        /// <summary>
        /// Gets the security type for the specified symbol
        /// </summary>
        /// <param name="symbol">The symbol</param>
        /// <returns>The security type</returns>
        public SecurityType GetSecurityType(string symbol)
        {
            return _instruments[symbol].Type;
        }

        /// <summary>
        /// Get historical data enumerable for a single symbol, type and resolution given this start and end time (in UTC).
        /// </summary>
        /// <param name="symbol">Symbol for the data we're looking for.</param>
        /// <param name="type">Security type</param>
        /// <param name="resolution">Resolution of the data request</param>
        /// <param name="startUtc">Start time of the data in UTC</param>
        /// <param name="endUtc">End time of the data in UTC</param>
        /// <returns>Enumerable of base data for this symbol</returns>
        public IEnumerable<BaseData> Get(Symbol symbol, SecurityType type, Resolution resolution, DateTime startUtc, DateTime endUtc)
        {
            if (!_instruments.ContainsKey(symbol.Value))
                throw new ArgumentException("Invalid symbol requested: " + symbol.ToString());

            if (resolution == Resolution.Tick)
                throw new NotSupportedException("Resolution not available: " + resolution);

            if (type != SecurityType.Forex && type != SecurityType.Cfd)
                throw new NotSupportedException("SecurityType not available: " + type);

            if (endUtc < startUtc)
                throw new ArgumentException("The end date must be greater or equal than the start date.");

            var barsTotalInPeriod = new List<Candle>();
            var barsToSave = new List<Candle>();

            // set the starting date/time
            DateTime date = startUtc;
            DateTime startDateTime = date;

            // loop until last date
            while (startDateTime <= endUtc.AddDays(1))
            {
                string start = startDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

                // request blocks of 5-second bars with a starting date/time
                var oandaSymbol = ConvertLeanSymbolToOandaSymbol(symbol);
                var bars = DownloadBars(oandaSymbol, start, BarsPerRequest);
                if (bars.Count == 0)
                    break;

                var groupedBars = GroupBarsByDate(bars);

                if (groupedBars.Count > 1)
                {
                    // we received more than one day, so we save the completed days and continue
                    while (groupedBars.Count > 1)
                    {
                        var currentDate = groupedBars.Keys.First();
                        if (currentDate > endUtc)
                            break;

                        barsToSave.AddRange(groupedBars[currentDate]);

                        barsTotalInPeriod.AddRange(barsToSave);

                        barsToSave.Clear();

                        // remove the completed date 
                        groupedBars.Remove(currentDate);
                    }

                    // update the current date
                    date = groupedBars.Keys.First();

                    if (date <= endUtc)
                    {
                        barsToSave.AddRange(groupedBars[date]);
                    }
                }
                else
                {
                    var currentDate = groupedBars.Keys.First();
                    if (currentDate > endUtc)
                        break;

                    // update the current date
                    date = currentDate;

                    barsToSave.AddRange(groupedBars[date]);
                }

                // calculate the next request datetime (next 5-sec bar time)
                startDateTime = GetDateTimeFromString(bars[bars.Count - 1].time).AddSeconds(5);
            }

            if (barsToSave.Count > 0)
            {
                barsTotalInPeriod.AddRange(barsToSave);
            }

            switch (resolution)
            {
                case Resolution.Second:
                    foreach (var bar in AggregateBars(symbol, barsTotalInPeriod, new TimeSpan(0, 0, 1)))
                    {
                        yield return bar;
                    }
                    break;

                case Resolution.Minute:
                    foreach (var bar in AggregateBars(symbol, barsTotalInPeriod, new TimeSpan(0, 1, 0)))
                    {
                        yield return bar;
                    }
                    break;

                case Resolution.Hour:
                    foreach (var bar in AggregateBars(symbol, barsTotalInPeriod, new TimeSpan(1, 0, 0)))
                    {
                        yield return bar;
                    }
                    break;

                case Resolution.Daily:
                    foreach (var bar in AggregateBars(symbol, barsTotalInPeriod, new TimeSpan(1, 0, 0, 0)))
                    {
                        yield return bar;
                    }
                    break;
            }
        }

        /// <summary>
        /// Aggregates a list of 5-second bars at the requested resolution
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="bars"></param>
        /// <param name="resolution"></param>
        /// <returns></returns>
        private static IEnumerable<TradeBar> AggregateBars(Symbol symbol, List<Candle> bars, TimeSpan resolution)
        {
            return
                (from b in bars
                 group b by GetDateTimeFromString(b.time).RoundDown(resolution)
                     into g
                     select new TradeBar
                     {
                         Symbol = symbol,
                         Time = g.Key,
                         Open = Convert.ToDecimal(g.First().openMid),
                         High = Convert.ToDecimal(g.Max(b => b.highMid)),
                         Low = Convert.ToDecimal(g.Min(b => b.lowMid)),
                         Close = Convert.ToDecimal(g.Last().closeMid)
                     });
        }

        /// <summary>
        /// Groups a list of bars into a dictionary keyed by date
        /// </summary>
        /// <param name="bars"></param>
        /// <returns></returns>
        private static SortedDictionary<DateTime, List<Candle>> GroupBarsByDate(List<Candle> bars)
        {
            var groupedBars = new SortedDictionary<DateTime, List<Candle>>();

            foreach (var bar in bars)
            {
                var date = GetDateTimeFromString(bar.time).Date;

                if (!groupedBars.ContainsKey(date))
                    groupedBars[date] = new List<Candle>();

                groupedBars[date].Add(bar);
            }

            return groupedBars;
        }

        /// <summary>
        /// Returns a DateTime from an RFC3339 string (with microsecond resolution)
        /// </summary>
        /// <param name="time"></param>
        private static DateTime GetDateTimeFromString(string time)
        {
            return DateTime.ParseExact(time, "yyyy-MM-dd'T'HH:mm:ss.000000'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Downloads a block of 5-second bars from a starting datetime
        /// </summary>
        /// <param name="oandaSymbol"></param>
        /// <param name="start"></param>
        /// <param name="barsPerRequest"></param>
        /// <returns></returns>
        private static List<Candle> DownloadBars(string oandaSymbol, string start, int barsPerRequest)
        {
            var request = new CandlesRequest
            {
                instrument = oandaSymbol,
                granularity = EGranularity.S5,
                candleFormat = ECandleFormat.midpoint,
                count = barsPerRequest,
                start = Uri.EscapeDataString(start)
            };
            return GetCandles(request);
        }

        /// <summary>
        /// More detailed request to retrieve candles
        /// </summary>
        /// <param name="request">the request data to use when retrieving the candles</param>
        /// <returns>List of Candles received (or empty list)</returns>
        public static List<Candle> GetCandles(CandlesRequest request)
        {
            string requestString = Credentials.GetDefaultCredentials().GetServer(EServer.Rates) + request.GetRequestString();

            CandlesResponse candlesResponse = MakeRequest<CandlesResponse>(requestString);
            List<Candle> candles = new List<Candle>();
            if (candlesResponse != null)
            {
                candles.AddRange(candlesResponse.candles);
            }
            return candles;
        }

        /// <summary>
        /// Primary (internal) request handler
        /// </summary>
        /// <typeparam name="T">The response type</typeparam>
        /// <param name="requestString">the request to make</param>
        /// <param name="method">method for the request (defaults to GET)</param>
        /// <param name="requestParams">optional parameters (note that if provided, it's assumed the requestString doesn't contain any)</param>
        /// <returns>response via type T</returns>
        private static T MakeRequest<T>(string requestString, string method = "GET", Dictionary<string, string> requestParams = null)
        {
            if (requestParams != null && requestParams.Count > 0)
            {
                var parameters = CreateParamString(requestParams);
                requestString = requestString + "?" + parameters;
            }
            HttpWebRequest request = WebRequest.CreateHttp(requestString);
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + Credentials.GetDefaultCredentials().AccessToken;
            request.Headers[HttpRequestHeader.AcceptEncoding] = "gzip, deflate";
            request.Method = method;

            try
            {
                using (WebResponse response = request.GetResponse())
                {
                    var stream = GetResponseStream(response);
                    var reader = new StreamReader(stream);
                    var result = reader.ReadToEnd();
                    return JsonConvert.DeserializeObject<T>(result);
                }
            }
            catch (WebException ex)
            {
                var stream = GetResponseStream(ex.Response);
                var reader = new StreamReader(stream);
                var result = reader.ReadToEnd();
                throw new Exception(result);
            }
        }

        private static Stream GetResponseStream(WebResponse response)
        {
            var stream = response.GetResponseStream();
            if (response.Headers["Content-Encoding"] == "gzip")
            {	// if we received a gzipped response, handle that
                stream = new GZipStream(stream, CompressionMode.Decompress);
            }
            return stream;
        }

        /// <summary>
        /// Helper function to create the parameter string out of a dictionary of parameters
        /// </summary>
        /// <param name="requestParams">the parameters to convert</param>
        /// <returns>string containing all the parameters for use in requests</returns>
        private static string CreateParamString(Dictionary<string, string> requestParams)
        {
            return string.Join(",", requestParams.Select(x => x.Key + "=" + x.Value).Select(WebUtility.UrlEncode));
        }


    }
}
