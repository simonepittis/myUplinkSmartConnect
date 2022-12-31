﻿using LiteDB;
using xElectricityPriceApi.Models;
using xElectricityPriceApiShared;
using xElectricityPriceApiShared.Model;

namespace xElectricityPriceApi.Services
{
    public class PriceService
    {
        readonly ILiteCollection<PriceInformation> _pricePointCol;
        readonly ILiteCollection<AveragePrice> _priceAvarageCol;

        public PriceService(LiteDBService db) 
        {
            _pricePointCol = db.GetCollection<PriceInformation>();
            _priceAvarageCol = db.GetCollection<AveragePrice>();
        }

        public void AddOrUpdate(PricePoint pricePoint)
        {
            var item = new PriceInformation()
            {
                Price = pricePoint.Price,
                End = pricePoint.End,
                Id = pricePoint.Id,
                SouceApi = pricePoint.SouceApi,
                Start = pricePoint.Start,
                StartHour = pricePoint.Start.Hour
            };

            AddOrUpdate(item);
        }

        public void AddOrUpdate(PriceInformation pricePoint)
        {
            if(_pricePointCol.FindById(pricePoint.Id) == null)
            {
                _pricePointCol.Insert(pricePoint);
            }
            else
            {
                _pricePointCol.Update(pricePoint);
            }
        }

        public AveragePrice GetAverageForMonth()
        {
            var avr = _priceAvarageCol.FindOne(Query.EQ(nameof(AveragePrice.Point), DateTime.Now.Date));
            return avr;
        }

        public IEnumerable<PriceInformation> GetAllThisMonth()
        {
            var start = new DateTime(DateTime.Now.Year, DateTime.Now.Month,1);
            var end = DateTime.Now.Date.AddDays(2).AddSeconds(-1);

            var priceList = _pricePointCol.Find(Query.Between(nameof(PriceInformation.Start), start, end)).ToArray();
            return priceList;
        }

        public IEnumerable<ExtendedPriceInformation> GetAllTodayAndTomorrow()
        {
            const double ElectricitySupportStart = 0.875d;
            const double ElectricitySupportPercentage = 0.90d;

            var start = DateTime.Now.Date;
            var end = start.AddDays(2).AddSeconds(-1);

            var tmpPriceList = _pricePointCol.Find(Query.Between(nameof(PriceInformation.Start), start, end)).ToArray();
            var avarage = GetAverageForMonth();
            var priceList = JsonUtils.CloneTo<ExtendedPriceInformation[]>(tmpPriceList);

            var electricitySupportPayBackPrKw = (avarage.Price - ElectricitySupportStart) * ElectricitySupportPercentage;

            foreach (var price in priceList)
            {
                price.PriceAfterSupport = price.Price - electricitySupportPayBackPrKw;
            }

            return priceList;
        }

        public void AddOrUpdate(AveragePrice averagePrice)
        {
            if (_priceAvarageCol.FindById(averagePrice.Id) == null)
            {
                _priceAvarageCol.Insert(averagePrice);
            }
            else
            {
                _priceAvarageCol.Update(averagePrice);
            }
        }

        public int AveragePriceCount
        {
            get
            {
                return _priceAvarageCol.Count();
            }
        }
    }
}