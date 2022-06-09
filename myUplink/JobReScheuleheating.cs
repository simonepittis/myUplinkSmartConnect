﻿using MyUplinkSmartConnect.ExternalPrice;
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
            var powerPrice = new EntsoeAPI();
            var priceInformation = await powerPrice.FetchPriceInformation();
            if(powerPrice.PriceList.Count >= 48)
            {
                return powerPrice;
            }
            

            Log.Logger.Error("Failed to get updated price inforamtion, from EU API. Checking nordpool");

            var nordpoolGroup = new Nordpoolgroup();
            await nordpoolGroup.GetPriceInformation();

            if (nordpoolGroup.PriceList.Count >= 48)
            {
                return nordpoolGroup;
            }

            return null;
        }

        public static async Task<bool> Work()
        {
            var priceInformation = await GetPriceInformation();
            if (priceInformation == null)
                return false;

            var cleanDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);

            priceInformation.CreateSortedList(cleanDate, Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);
            priceInformation.CreateSortedList(cleanDate.AddDays(1), Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);
            priceInformation.PrintScheudule();

            var group = await Settings.Instance.myuplinkApi.GetDevices();
            foreach (var device in group)
            {
                if (device.devices == null)
                {
                    Log.Logger.Error($"Group({device.id}) does not have devices");
                    continue;
                }

                foreach (var tmpdevice in device.devices)
                {
                    var costSaving = new ApplyCostSavingRules();
                    costSaving.WaterHeaterSchedule = await Settings.Instance.myuplinkApi.GetWheeklySchedules(tmpdevice);
                    costSaving.WaterHeaterModes = await Settings.Instance.myuplinkApi.GetCurrentModes(tmpdevice);

                    if (!costSaving.VerifyWaterHeaterModes())
                    {
                        var status = await Settings.Instance.myuplinkApi.SetCurrentModes(tmpdevice, costSaving.WaterHeaterModes);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater modes, aborting");
                            return false;
                        }
                    }

                    

                    if (!costSaving.VerifyHeaterSchedule(priceInformation.PriceList, cleanDate, cleanDate.AddDays(1)))
                    {
                        var status = await Settings.Instance.myuplinkApi.SetWheeklySchedules(tmpdevice, costSaving.WaterHeaterSchedule);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater schedule, aborting");
                            return false;
                        }
                        else
                        {
                            Log.Logger.Information($"Changed schedule for {tmpdevice.id}");

                            if(!string.IsNullOrEmpty(Settings.Instance.MQTTServer) && !string.IsNullOrEmpty(device.name))
                            {
                                var job = new JobCheckHeaterStatus();
                                await job.SendUpdate(device.name, Models.CurrentPointParameterType.LastScheduleChangeInHours, Convert.ToInt32(0));
                            }
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
