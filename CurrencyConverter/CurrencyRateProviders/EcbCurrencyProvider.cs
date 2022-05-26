﻿using System.Globalization;
using System.Reflection.Metadata.Ecma335;
using CsvHelper;

namespace CurrencyConverter.CurrencyRateProviders;

public class EcbCurrencyProvider : ICurrencyRateProvider
{
    private static readonly Dictionary<Currency, YearlyCurrencyRates> _rates = new(); // Euro to Currency

    /// <inheritdoc />
    public bool CanHandle(Currency sourceCurrency, Currency targetCurrency)
    {
        if (sourceCurrency == targetCurrency)
            return true;

        return targetCurrency is <= Currency.MXN and not Currency.UAH and not Currency.CLP &&
               sourceCurrency is <= Currency.MXN and not Currency.UAH and not Currency.CLP; // All currencies supported by ECB
    }

    /// <inheritdoc />
    public decimal GetRate(Currency sourceCurrency, Currency targetCurrency, DateTime date)
    {
        if (DateTime.Now.Date < date)
        {
            throw new ArgumentException("Date cannot be in the future");
        }

        if (sourceCurrency == targetCurrency)
        {
            return 1m;
        }

        if (sourceCurrency != Currency.EUR)
        {
            return GetRate(Currency.EUR, targetCurrency, date) / GetRate(Currency.EUR, sourceCurrency, date);
        }

        if (!CanHandle(sourceCurrency, targetCurrency))
        {
            throw
                new NotSupportedException("Conversion not supported by ECB"); // TODO: Create CurrencyConversionNotSupportedException
        }

        if (!_rates.TryGetValue(targetCurrency, out var yearlyRates))
        {
            yearlyRates = new YearlyCurrencyRates(Currency.EUR, targetCurrency);
            _rates.Add(targetCurrency, yearlyRates);
        }

        EnsureRateIsCached(targetCurrency, date, yearlyRates);

        return yearlyRates.GetRate(date);
    }

    /// <inheritdoc />
    public bool TryGetRate(Currency sourceCurrency, Currency targetCurrency, DateTime date, out decimal rate)
    {
        rate = default;
        if (sourceCurrency == targetCurrency)
        {
            rate = 1m;
            return true;
        }

        if (sourceCurrency != Currency.EUR)
        {
            if (!TryGetRate(Currency.EUR, sourceCurrency, date, out var eurSrcRate) ||
                !TryGetRate(Currency.EUR, targetCurrency, date, out var eurTargetRate))
            {
                return false;
            }
            rate = eurTargetRate / eurSrcRate;
            return true;
        }

        if (!CanHandle(sourceCurrency, targetCurrency) || DateTime.Now.Date < date)
        {
            return false;
        }

        if (!_rates.TryGetValue(targetCurrency, out var yearlyRates))
        {
            yearlyRates = new YearlyCurrencyRates(Currency.EUR, targetCurrency);
            _rates.Add(targetCurrency, yearlyRates);
        }

        if (!TryCacheRate(targetCurrency, date, yearlyRates))
        {
            return false;
        }
        return yearlyRates.TryGetRate(date, out rate);
    }

    private bool TryCacheRate(Currency targetCurrency, DateTime date, YearlyCurrencyRates yearlyRates)
    {
        if (targetCurrency == Currency.EUR) return true;

        if (!yearlyRates.ContainsRate(date))
        {
            if (!TryGetEcbRates(date.Year, targetCurrency))
            {
                return false;
            }

            if (yearlyRates.ContainsRate(date))
            {
                return true;
            }
            
            if (!TryGetEcbRates(date.Year - 1, targetCurrency))
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureRateIsCached(Currency targetCurrency, DateTime rateDate, YearlyCurrencyRates yearlyRates)
    {
        if (targetCurrency == Currency.EUR) return;

        if (EnsureYearIsCached(targetCurrency, rateDate, rateDate.Year, yearlyRates))
        {
            return;
        }

        EnsureYearIsCached(targetCurrency, rateDate, rateDate.Year - 1, yearlyRates);
    }

    private bool EnsureYearIsCached(Currency sourceCurrency, DateTime rateDate, int year, YearlyCurrencyRates yearlyRates)
    {
        if (yearlyRates.ContainsRate(rateDate))
        {
            return true;
        }

        if (!TryGetEcbRates(year, sourceCurrency))
        {
            throw new Exception($"Could not get ECB rates for year {year}");
        }

        return false;
    }

    private bool TryGetEcbRates(int year, Currency targetCurrency)
    {
        if (!_rates.TryGetValue(targetCurrency, out var yearlyRates))
        {
            throw new InvalidDataException("Rates not found"); // this should never happen
        }

        var client = new HttpClient();
        var response = client.GetAsync(PrepareQuery(targetCurrency, Currency.EUR, year)).Result;
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        yearlyRates.SetRates((ushort)year, ParseRecords(response.Content));
        return true;
    }

    private IEnumerable<KeyValuePair<DateTime, decimal>> ParseRecords(HttpContent content)
    {
        using (var reader = new StreamReader(content.ReadAsStream()))
        {
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                return csv.GetRecords<CsvConversionData>()
                    .Select(x => new KeyValuePair<DateTime, decimal>(x.TIME_PERIOD, x.OBS_VALUE)).ToList();
            }
        }
    }


    private string PrepareQuery(Currency sourceCurrency, Currency targetCurrency, int year)
    {
        return
            $"https://sdw-wsrest.ecb.europa.eu/service/data/EXR/D.{sourceCurrency}.EUR.SP00.A?startPeriod={year}-01-01&endPeriod={year}-12-31&format=csvdata";
    }

    public void ClearCache()
    {
        _rates.Clear();
    }

    internal struct CsvConversionData
    {
        public DateTime TIME_PERIOD { get; set; }
        public decimal OBS_VALUE { get; set; }
    }
}
