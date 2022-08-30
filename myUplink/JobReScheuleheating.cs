﻿using MyUplinkSmartConnect.CostSavings;
using MyUplinkSmartConnect.CostSavingsRules;
using MyUplinkSmartConnect.ExternalPrice;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyUplinkSmartConnect
{
    public class JobReScheuleheating
    {
        static async Task<iBasePriceInformation?> GetPriceInformation()
        {
            var priceFetchApiList = new iBasePriceInformation[] { new EntsoeAPI(), new Nordpoolgroup(), new VgApi() };

            foreach(var priceListApi in priceFetchApiList)
            {
                var status = await priceListApi.GetPriceInformation();

                if(status && CurrentState.PriceList.Count >= 48)
                {
                    Log.Logger.Debug("Using {priceApi} price list", priceListApi.GetType());
                    return priceListApi;
                }
            }

            Log.Logger.Warning("Failed to get price information for today and tomorrow, will check for todays prices");
            foreach (var priceListApi in priceFetchApiList)
            {
                var status = await priceListApi.GetPriceInformation();

                if (status && CurrentState.PriceList.Count >= 24)
                {
                    Log.Logger.Debug("Using {priceApi} price list for today", priceListApi.GetType());
                    return priceListApi;
                }
            }

            Log.Logger.Warning("Failed to price list from all known apis, will check schedule later.");
            return null;
        }

        public static async Task<bool> Work()
        {
            var priceInformation = await GetPriceInformation();
            if (priceInformation == null)
                return false;

            bool hasTomorrowElectricityPrice = (CurrentState.PriceList.Count >= 48);
            var cleanDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);

            priceInformation.CreateSortedList(cleanDate, Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);

            if(hasTomorrowElectricityPrice)
            {
                priceInformation.CreateSortedList(cleanDate.AddDays(1), Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);
            }
            
            var group = await Settings.Instance.myuplinkApi.GetDevices();
            foreach (var device in group)
            {
                if (device.devices == null)
                {
                    Log.Logger.Error("Group({DeviceId}) does not have devices",device.id);
                    continue;
                }

                foreach (var tmpdevice in device.devices)
                {
                    ICostSavingRules costSaving;
                    if(Settings.Instance.EnergiBasedCostSaving)
                    {
                        costSaving = new EnergiBasedRules();
                        Log.Logger.Debug("Using energi based rules for building heating schedules");
                    }
                    else
                    {
                        costSaving = new SimplePriceBasedRules();
                        Log.Logger.Debug("Using simple price based rules for building heating schedules");
                    }

                    costSaving.WaterHeaterSchedule = await Settings.Instance.myuplinkApi.GetWheeklySchedules(tmpdevice);
                    costSaving.WaterHeaterModes = await Settings.Instance.myuplinkApi.GetCurrentModes(tmpdevice);
                    var weekdayOrder = Settings.Instance.myuplinkApi.GetCurrentDayOrder(tmpdevice);

                    if (!costSaving.VerifyWaterHeaterModes())
                    {
                        var status = await Settings.Instance.myuplinkApi.SetCurrentModes(tmpdevice, costSaving.WaterHeaterModes);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater modes, aborting");
                            return false;
                        }
                    }                    

                    if (hasTomorrowElectricityPrice && costSaving.GenerateSchedule(weekdayOrder, cleanDate, cleanDate.AddDays(1))  || 
                        !hasTomorrowElectricityPrice && costSaving.GenerateSchedule(weekdayOrder, cleanDate))
                    {                        
#if DEBUG
                        costSaving.LogToCSV();
#endif
                        costSaving.LogSchedule();

                        var status = await Settings.Instance.myuplinkApi.SetWheeklySchedules(tmpdevice, costSaving.WaterHeaterSchedule);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater schedule, aborting");
                            return false;
                        }
                        else
                        {
                            if(hasTomorrowElectricityPrice)
                                Log.Logger.Information("Changed schedule for {DeviceId} for today and tomorrow",tmpdevice.id);
                            else
                                Log.Logger.Information("Changed schedule for {DeviceId} for today", tmpdevice.id);

                            if (!string.IsNullOrEmpty(Settings.Instance.MQTTServer) && !string.IsNullOrEmpty(device.name))
                            {
                                await Settings.Instance.MQTTSender.SendUpdate(device.name, Models.CurrentPointParameterType.LastScheduleChangeInHours, Convert.ToInt32(0));
                            }
                            return hasTomorrowElectricityPrice; // If we did not get tomorrows prices we return false so we can try the schedule again.
                        }
                    }
                }
            }
            return false;
        }
    }
}