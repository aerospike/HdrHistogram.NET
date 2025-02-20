using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HdrHistogram.Utilities;

namespace HdrHistogram
{
    /// <summary>
    /// Reads a log of Histograms from the provided <see cref="Stream"/>.
    /// </summary>
    public sealed class HistogramLogReader : IDisposable, IHistogramLogV1Reader
    {
        //TODO: I think this should be able to cater for 0dp -LC
        private static readonly Regex StartTimeMatcher = new Regex(@"#\[StartTime: (?<seconds>\d*\.\d{1,3}) ", RegexOptions.Compiled);
        private static readonly Regex BaseTimeMatcher = new Regex(@"#\[BaseTime: (?<seconds>\d*\.\d{1,3}) ", RegexOptions.Compiled);
        //Content lines - format =  startTimestamp, intervalLength, maxTime, histogramPayload
        private static readonly Regex UntaggedLogLineMatcher = new Regex(@"(?<startTime>\d*\.\d*),(?<interval>\d*\.\d*),(?<max>\d*\.\d*),(?<payload>.*)", RegexOptions.Compiled);
        private static readonly Regex TaggedLogLineMatcher = new Regex(@"((?<tag>Tag=.+),)?(?<startTime>\d*\.\d*),(?<interval>\d*\.\d*),(?<max>\d*\.\d*),(?<payload>.*)", RegexOptions.Compiled);
        private readonly TextReader _log;
        private double _startTimeInSeconds;

        /// <summary>
        /// Reads each histogram out from the underlying stream.
        /// </summary>
        /// <param name="inputStream">The <see cref="Stream"/> to read from.</param>
        /// <returns>Return a lazily evaluated sequence of histograms.</returns>
        public static IEnumerable<HistogramBase> Read(Stream inputStream)
        {
            using var reader = new HistogramLogReader(inputStream);
            
            foreach(var histogram in reader.ReadHistograms())
            {
                yield return histogram;
            }            
        }

        /// <summary>
        /// Creates a <see cref="HistogramLogReader"/> that reads from the provided <see cref="Stream"/>.
        /// </summary>
        /// <param name="inputStream">The <see cref="Stream"/> to read from.</param>
        public HistogramLogReader(Stream inputStream)
        {
            _log = new StreamReader(inputStream, System.Text.Encoding.UTF8, true, 1024, true);
        }

        /// <summary>
        /// Reads each histogram out from the underlying stream.
        /// </summary>
        /// <returns>Return a lazily evaluated sequence of histograms.</returns>
        public IEnumerable<HistogramBase> ReadHistograms()
        {
            _startTimeInSeconds = 0;
            double baseTimeInSeconds = 0;
            bool hasStartTime = false;
            bool hasBaseTime = false;
            foreach (var line in ReadLines())
            {
                //Comments (and header metadata)
                if (IsComment(line))
                {
                    if (IsStartTime(line))
                    {
                        _startTimeInSeconds = ParseStartTime(line);
                        hasStartTime = true;
                    }
                    else if (IsBaseTime(line))
                    {
                        baseTimeInSeconds = ParseBaseTime(line);
                        hasBaseTime = true;
                    }
                }
                //Legend/Column headers
                else if (IsLegend(line))
                {
                    //Ignore
                }

                else
                {
                    //Content lines - format =  startTimestamp, intervalLength, maxTime, histogramPayload

                    var match = TaggedLogLineMatcher.Match(line);
                    var tag = ParseTag(match.Groups["tag"].Value);
                    var logTimeStampInSec = ParseDouble(match, "startTime");
                    var intervalLength = ParseDouble(match, "interval");
                    var maxTime = ParseDouble(match, "max");    //Ignored as it can be inferred -LC
                    var payload = match.Groups["payload"].Value;

                    if (!hasStartTime)
                    {
                        // No explicit start time noted. Use 1st observed time:
                        _startTimeInSeconds = logTimeStampInSec;
                        hasStartTime = true;
                    }
                    if (!hasBaseTime)
                    {
                        // No explicit base time noted. Deduce from 1st observed time (compared to start time):
                        if (logTimeStampInSec < _startTimeInSeconds - (365 * 24 * 3600.0))
                        //if (UnixTimeExtensions.ToDateFromSecondsSinceEpoch(logTimeStampInSec) < startTime.AddYears(1))
                        {
                            // Criteria Note: if log timestamp is more than a year in the past (compared to
                            // StartTime), we assume that timestamps in the log are not absolute
                            baseTimeInSeconds = _startTimeInSeconds;
                        }
                        else
                        {
                            // Timestamps are absolute
                            baseTimeInSeconds = 0.0;
                        }
                        hasBaseTime = true;
                    }

                    double absoluteStartTimeStampSec = logTimeStampInSec + baseTimeInSeconds;
                    double absoluteEndTimeStampSec = absoluteStartTimeStampSec + intervalLength;


                    byte[] bytes = Convert.FromBase64String(payload);
                    var buffer = ByteBuffer.Allocate(bytes);
                    var histogram = DecodeHistogram(buffer, 0);
                    histogram.Tag = tag;
                    histogram.StartTimeStamp = (long)(absoluteStartTimeStampSec * 1000.0);
                    histogram.EndTimeStamp = (long)(absoluteEndTimeStampSec * 1000.0);
                    yield return histogram;
                }
            }
        }
        
        IEnumerable<HistogramBase> IHistogramLogV1Reader.ReadHistograms()
        {
            _startTimeInSeconds = 0;
            double baseTimeInSeconds = 0;
            bool hasStartTime = false;
            bool hasBaseTime = false;
            foreach (var line in ReadLines())
            {
                //Comments (and header metadata)
                if (IsComment(line))
                {
                    if (IsStartTime(line))
                    {
                        _startTimeInSeconds = ParseStartTime(line);
                        hasStartTime = true;
                    }
                    else if (IsBaseTime(line))
                    {
                        baseTimeInSeconds = ParseBaseTime(line);
                        hasBaseTime = true;
                    }
                }
                //Legend/Column headers
                else if (IsV1Legend(line))
                {
                    //Ignore
                }

                else
                {
                    //Content lines - format =  startTimestamp, intervalLength, maxTime, histogramPayload

                    var match = UntaggedLogLineMatcher.Match(line);
                    var logTimeStampInSec = ParseDouble(match, "startTime");
                    var intervalLength = ParseDouble(match, "interval");
                    var maxTime = ParseDouble(match, "max");    //Ignored as it can be inferred -LC
                    var payload = match.Groups["payload"].Value;
                    

                    if (!hasStartTime)
                    {
                        // No explicit start time noted. Use 1st observed time:
                        _startTimeInSeconds = logTimeStampInSec;
                        hasStartTime = true;
                    }
                    if (!hasBaseTime)
                    {
                        // No explicit base time noted. Deduce from 1st observed time (compared to start time):
                        if (logTimeStampInSec < _startTimeInSeconds - (365 * 24 * 3600.0))
                        {
                            // Criteria Note: if log timestamp is more than a year in the past (compared to
                            // StartTime), we assume that timestamps in the log are not absolute
                            baseTimeInSeconds = _startTimeInSeconds;
                        }
                        else
                        {
                            // Timestamps are absolute
                            baseTimeInSeconds = 0.0;
                        }
                        hasBaseTime = true;
                    }

                    double absoluteStartTimeStampSec = logTimeStampInSec + baseTimeInSeconds;
                    double absoluteEndTimeStampSec = absoluteStartTimeStampSec + intervalLength;


                    byte[] bytes = Convert.FromBase64String(payload);
                    var buffer = ByteBuffer.Allocate(bytes);
                    var histogram = DecodeHistogram(buffer, 0);
                    histogram.StartTimeStamp = (long)(absoluteStartTimeStampSec * 1000.0);
                    histogram.EndTimeStamp = (long)(absoluteEndTimeStampSec * 1000.0);
                    yield return histogram;
                }
            }
        }

        /// <summary>
        /// Gets the start time for the set of Histograms.
        /// </summary>
        /// <returns>Either the explicit encoded start time, or falls back to the start time of the first histogram.</returns>
        /// <remarks>
        /// The current implementation requires the consumer to only use this after enumerating the Histograms from the <see cref="ReadHistograms()"/> method.
        /// </remarks>
        public DateTime GetStartTime()
        {
            //NOTE: It would be good if this could expose a DateTimeOffset, however currently that would be misleading, as that level of fidelity is not captured. -LC

            //If StartTime was set (#[StartTime:...) use it, else use the first `logTimestampInSec`
            //This method is odd, in that it only works if the file has been read. That is a bit shit. -LC

            //TODO: Create an API that allows GetStartTime to be deterministic (not dependant on how far through ReadHistograms you have enumerated.

            return _startTimeInSeconds.ToDateFromSecondsSinceEpoch();
        }

        private static HistogramBase DecodeHistogram(ByteBuffer buffer, long minBarForHighestTrackableValue)
        {
            return HistogramEncoding.DecodeFromCompressedByteBuffer(buffer, minBarForHighestTrackableValue);
        }

        private IEnumerable<string> ReadLines()
        {
            while (true)
            {
                var line = _log.ReadLine();
                if (line == null)
                    yield break;
                yield return line;
            }
        }

        private static bool IsComment(string line)
        {
            return line.StartsWith("#");
        }

        private static bool IsStartTime(string line)
        {
            return line.StartsWith("#[StartTime: ");
        }

        private static bool IsBaseTime(string line)
        {
            return line.StartsWith("#[BaseTime: ");
        }

        private static bool IsLegend(string line)
        {
            var legend = "\"StartTimestamp\",\"Interval_Length\",\"Interval_Max\",\"Interval_Compressed_Histogram\"";
            return line.Equals(legend);
        }
        private static bool IsV1Legend(string line)
        {
            var legend = "\"StartTimestamp\",\"EndTimestamp\",\"Interval_Max\",\"Interval_Compressed_Histogram\"";
            return line.Equals(legend);
        }

        private static string ParseTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = value.Substring(4);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value;
        }

        private static double ParseStartTime(string line)
        {
            var match = StartTimeMatcher.Match(line);
            return ParseDouble(match, "seconds");
        }

        private static double ParseBaseTime(string line)
        {
            var match = BaseTimeMatcher.Match(line);
            return ParseDouble(match, "seconds");
        }

        private static double ParseDouble(Match match, string group)
        {
            var value = match.Groups[group].Value;
            return double.Parse(value);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            using (_log) { }
        }
    }
}