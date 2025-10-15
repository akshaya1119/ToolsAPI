﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERPToolsAPI.Data;
using Tools.Models;
using System.Text.Json;
using OfficeOpenXml.Style;
using OfficeOpenXml;
using System.Drawing;
using System.Reflection;
using Tools.Migrations;
using Tools.Services;
using Microsoft.CodeAnalysis;

namespace Tools.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ExtraEnvelopesController : ControllerBase
    {
        private readonly ERPToolsDbContext _context;
        private readonly ILoggerService _loggerService;

        public ExtraEnvelopesController(ERPToolsDbContext context, ILoggerService loggerService)
        {
            _context = context;
            _loggerService = loggerService;
        }

        // GET: api/ExtraEnvelopes
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ExtraEnvelopes>>> GetExtrasEnvelope(int ProjectId)
        {
            var NrData = await _context.NRDatas.Where(p => p.ProjectId == ProjectId).ToListAsync();

            return await _context.ExtrasEnvelope.ToListAsync();
        }

        // GET: api/ExtraEnvelopes/5
        [HttpGet("{id}")]
        public async Task<ActionResult<ExtraEnvelopes>> GetExtraEnvelopes(int id)
        {
            var extraEnvelopes = await _context.ExtrasEnvelope.FindAsync(id);

            if (extraEnvelopes == null)
            {
                return NotFound();
            }

            return extraEnvelopes;
        }

        // PUT: api/ExtraEnvelopes/5
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPut("{id}")]
        public async Task<IActionResult> PutExtraEnvelopes(int id, ExtraEnvelopes extraEnvelopes)
        {
            if (id != extraEnvelopes.Id)
            {
                return BadRequest();
            }

            _context.Entry(extraEnvelopes).State = EntityState.Modified;

            try
            {
                _loggerService.LogEvent($"Updated ExtraEnvelope with id {id}", "ExtraEnvelopes", User.Identity?.Name != null ? int.Parse(User.Identity.Name) : 0,extraEnvelopes.ProjectId);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                if (!ExtraEnvelopesExists(id))
                {
                    _loggerService.LogEvent($"ExtraEnvelopes with ID {id} not found during updating", "ExtraEnvelopes", User.Identity?.Name != null ? int.Parse(User.Identity.Name) : 0, extraEnvelopes.ProjectId);
                    return NotFound();
                }
                else
                {
                    _loggerService.LogError("Error updating ExtraEnvelopes", ex.Message, nameof(ExtraEnvelopesController));
                    throw;
                }
            }

            return NoContent();
        }

        public class EnvelopeType
        {
            public string Inner { get; set; }
            public string Outer { get; set; }
        }


        // POST: api/ExtraEnvelopes
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPost]
        public async Task<ActionResult> PostExtraEnvelopes(int ProjectId)
        {
            try
            {
                var nrDataList = await _context.NRDatas
                    .Where(d => d.ProjectId == ProjectId)
                    .ToListAsync();

                var groupedNR = nrDataList
                    .GroupBy(x => x.CatchNo)
                    .Select(g => new
                    {
                        CatchNo = g.Key,
                        Quantity = g.Sum(x => x.Quantity)
                    })
                    .ToList();
                if (!groupedNR.Any())
                {
                    return BadRequest("No grouping found");
                }

                var extraConfig = await _context.ExtraConfigurations
                    .Where(c => c.ProjectId == ProjectId)
                    .ToListAsync();

                if (!extraConfig.Any())
                    return BadRequest("No ExtraConfiguration found for the project.");

                var existingCombinations = await _context.ExtrasEnvelope
                  .Where(e => e.ProjectId == ProjectId)
                  .ToListAsync();


                if (existingCombinations.Any())
                {
                    // Remove existing duplicates
                    _context.ExtrasEnvelope.RemoveRange(existingCombinations);
                    await _context.SaveChangesAsync();
                }
                var envelopesToAdd = new List<ExtraEnvelopes>();
                foreach (var config in extraConfig)
                {
                    EnvelopeType envelopeType;
                    try
                    {
                        envelopeType = JsonSerializer.Deserialize<EnvelopeType>(config.EnvelopeType);
                    }
                    catch
                    {
                        return BadRequest($"Invalid EnvelopeType JSON for ExtraType {config.ExtraType}");
                    }

                    int innerCapacity = GetEnvelopeCapacity(envelopeType.Inner);
                    int outerCapacity = GetEnvelopeCapacity(envelopeType.Outer);

                    foreach (var data in groupedNR)
                    {
                        int calculatedQuantity = 0;
                        switch (config.Mode)
                        {
                            case "Fixed":
                                calculatedQuantity = int.Parse(config.Value);
                                break;
                            case "Percentage":
                                if (decimal.TryParse(config.Value, out var percentValue))
                                {
                                    // Step 1: Calculate raw percent of quantity
                                    var rawQuantity = (double)(data.Quantity * percentValue) / 100;
                                    // Step 3: Round up to next multiple of innerCapacity
                                    calculatedQuantity = (int)Math.Ceiling(rawQuantity / (double)innerCapacity) * innerCapacity;
                                }
                                else
                                {
                                    calculatedQuantity = 0; // Or apply fallback logic
                                }
                                break;
                        
                        }

                        int innerCount = (int)Math.Ceiling((double)calculatedQuantity / innerCapacity);
                        int outerCount = (int)Math.Ceiling((double)calculatedQuantity / outerCapacity);

                        envelopesToAdd.Add(new ExtraEnvelopes
                        {
                            ProjectId = ProjectId,
                            CatchNo = data.CatchNo,
                            ExtraId = config.ExtraType,
                            Quantity = calculatedQuantity,
                            InnerEnvelope = innerCount.ToString(),
                            OuterEnvelope = outerCount.ToString(),
                        });
                    }
                }

                await _context.ExtrasEnvelope.AddRangeAsync(envelopesToAdd);
                await _context.SaveChangesAsync();
                _loggerService.LogEvent($"Created ExtraEnvelopes for Project {ProjectId}", "ExtraEnvelopes", User.Identity?.Name != null ? int.Parse(User.Identity.Name) : 0,ProjectId);

                // ------------------- 📊 Generate Excel Report -------------------

                    var allNRData = await _context.NRDatas
                        .Where(x => x.ProjectId == ProjectId)
                        .ToListAsync();

                    var extraconfig = await _context.ExtraConfigurations
                        .Where(x => x.ProjectId == ProjectId).ToListAsync();
                    if (extraConfig == null || !extraConfig.Any())
                    {
                        _loggerService.LogError($"No ExtraConfiguration found for ProjectId: {ProjectId}", "", "ExtraEnvelopes");
                        return BadRequest("No ExtraConfiguration found for the project.");
                    }

                    var groupedByNodal = allNRData.GroupBy(x => x.NodalCode).ToList();

                    var extraHeaders = new HashSet<string>();
                    var innerKeys = new HashSet<string>();
                    var outerKeys = new HashSet<string>();

                    var allRows = new List<Dictionary<string, object>>();

                    foreach (var nodalGroup in groupedByNodal)
                    {
                        var catchNos = nodalGroup.Select(x => x.CatchNo).ToHashSet();
                        var extras1 = envelopesToAdd.Where(e => e.ExtraId == 1 && catchNos.Contains(e.CatchNo)).ToList();

                        foreach (var extra in extras1)
                        {
                            var baseRow = allNRData.FirstOrDefault(x => x.CatchNo == extra.CatchNo);
                            if (baseRow == null)
                            {
                                _loggerService.LogError($"Base row not found for CatchNo: {extra.CatchNo} in NRData", "", "ExtraEnvelopes");
                                continue; // Skip to the next entry
                            }


                            var config = extraConfig.FirstOrDefault(c => c.ExtraType == extra.ExtraId);
                            if (config == null) continue;

                            var dict = ExtraEnvelopeToDictionary(baseRow, extra, config, extraHeaders, innerKeys, outerKeys);
                            allRows.Add(dict);

                        }
                    }

                    // ➕ Add ExtraTypeId = 2 (University) and 3 (Office) at the end
                    foreach (var extraType in new[] { 2, 3 })
                    {
                        var extras = envelopesToAdd.Where(e => e.ExtraId == extraType).ToList();

                        foreach (var extra in extras)
                        {
                            var baseRow = allNRData.FirstOrDefault(x => x.CatchNo == extra.CatchNo);
                            if (baseRow == null)
                            {
                                _loggerService.LogError($"Base row not found for CatchNo: {extra.CatchNo} in NRData", "", "ExtraEnvelopes");
                                continue;
                            }
                            var config = extraConfig.FirstOrDefault(c => c.ExtraType == extra.ExtraId);
                            if (config == null) continue;

                            var dict = ExtraEnvelopeToDictionary(baseRow, extra, config, extraHeaders, innerKeys, outerKeys);
                            allRows.Add(dict);


                        }
                    }
                    if (!allRows.Any())
                    {
                        _loggerService.LogError("No valid rows found for Excel generation", "", "ExtraEnvelopes");
                        return BadRequest("No valid data to generate Excel report.");
                    }

                    // Create Excel
                    var allHeaders = typeof(Tools.Models.NRData).GetProperties().Select(p => p.Name).Where(p => p != "Id" && p != "ProjectId").ToList();
                    allHeaders.AddRange(extraHeaders.OrderBy(x => x));
                    allHeaders.AddRange(innerKeys.OrderBy(x => x));
                    allHeaders.AddRange(outerKeys.OrderBy(x => x));

                    var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", ProjectId.ToString());
                    if (!Directory.Exists(reportPath))
                    {
                        Directory.CreateDirectory(reportPath);
                    }
                    var filename = "ExtrasCalculation.xlsx";
                    var filePath = Path.Combine(reportPath, filename);

                    // 📁 Skip generation if file already exists
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                    var nonEmptyColumns = new List<string>();

                    // List to hold all rows after removing empty columns
                    var filteredRows = new List<Dictionary<string, object>>();

                    // Iterate over allRows to check for non-empty columns
                    try
                    {
                        foreach (var row in allRows)
                        {
                            var filteredRow = new Dictionary<string, object>();

                            foreach (var header in allHeaders)
                            {
                                if (row.ContainsKey(header))
                                {
                                    var value = row[header];
                                    if (value != null && !string.IsNullOrEmpty(value.ToString()))
                                    {
                                        filteredRow[header] = value;
                                        if (!nonEmptyColumns.Contains(header))
                                        {
                                            nonEmptyColumns.Add(header);  // Mark the column as non-empty
                                        }
                                    }
                                }
                            }

                            if (filteredRow.Any())  // Add row only if it has any data
                            {
                                filteredRows.Add(filteredRow);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggerService.LogError("Error processing rows for Excel file", ex.ToString(), "ExtraEnvelopesController");
                        throw new Exception("Error generating Excel report", ex); // Rethrow after logging
                    }

                    if (!filteredRows.Any())
                    {
                        _loggerService.LogError("No valid rows found after filtering", "", "ExtraEnvelopes");
                        return BadRequest("No valid data to save in the Excel file.");
                    }
                    try
                    {
                        using (var package = new ExcelPackage())
                        {
                            var ws = package.Workbook.Worksheets.Add("Extra Envelope");
                            if (!nonEmptyColumns.Any())
                            {
                                _loggerService.LogError("No non-empty columns found for Excel report", "", "ExtraEnvelopes");
                                return BadRequest("No data to write to Excel.");
                            }

                            var validHeaders = nonEmptyColumns;
                            for (int i = 0; i < validHeaders.Count; i++)
                            {
                                ws.Cells[1, i + 1].Value = validHeaders[i];
                                ws.Cells[1, i + 1].Style.Font.Bold = true;
                            }

                            int rowIdx = 2;
                            foreach (var row in filteredRows)
                            {
                                for (int colIdx = 0; colIdx < validHeaders.Count; colIdx++)
                                {
                                    var key = validHeaders[colIdx];
                                    row.TryGetValue(key, out object value);
                                    ws.Cells[rowIdx, colIdx + 1].Value = value?.ToString() ?? "";
                                }
                                rowIdx++;
                            }

                            ws.Cells[ws.Dimension.Address].AutoFitColumns();
                            package.SaveAs(new FileInfo(filePath));
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggerService.LogError("Error saving Excel file", ex.ToString(), "ExtraEnvelopesController");
                        throw new Exception("Error generating Excel report", ex); // Rethrow after logging
                    }

                    return Ok(envelopesToAdd);
                }
                catch (Exception ex)
                {
                    _loggerService.LogError("Error creating ExtraEnvelope", ex.Message, nameof(ExtraEnvelopesController));
                    return StatusCode(500, "Internal server error");
                }
            }

        private Dictionary<string, object> ExtraEnvelopeToDictionary(
       Tools.Models.NRData baseRow,
       ExtraEnvelopes extra,
       ExtrasConfiguration config,
       HashSet<string> extraKeys,
       HashSet<string> innerKeys,
       HashSet<string> outerKeys)
            {
                var dict = new Dictionary<string, object>
                {
                    ["CourseName"] = baseRow.CourseName,
                    ["SubjectName"] = baseRow.SubjectName,
                    ["CatchNo"] = baseRow.CatchNo,
                    ["ExamTime"] = baseRow.ExamTime,
                    ["ExamDate"] = baseRow.ExamDate,
                    ["NodalCode"] = baseRow.NodalCode,
                    ["NRDatas"] = baseRow.NRDatas,
                    ["Quantity"] = extra.Quantity,

                };

                // Set CenterCode
                switch (extra.ExtraId)
                {
                    case 1:
                        dict["CenterCode"] = "Nodal Extra";
                        break;
                    case 2:
                        dict["CenterCode"] = "University Extra";

                        break;
                    case 3:
                        dict["CenterCode"] = "Office Extra";

                        break;
                    default:
                        dict["CenterCode"] = "Extra";
                        break;
                }

                // Add NRDatas (extra fields)
                if (!string.IsNullOrEmpty(baseRow.NRDatas))
                {
                    try
                    {
                        var extras = JsonSerializer.Deserialize<Dictionary<string, string>>(baseRow.NRDatas);
                        if (extras != null)
                        {
                            foreach (var kvp in extras)
                            {
                                dict[kvp.Key] = kvp.Value;
                                extraKeys.Add(kvp.Key);
                            }
                        }
                    }
                    catch { }
                }

                // ✅ Set InnerEnvelope and OuterEnvelope values in proper columns
                EnvelopeType envelopeType;
                try
                {
                    envelopeType = JsonSerializer.Deserialize<EnvelopeType>(config.EnvelopeType);
                }
                catch
                {
                    envelopeType = new EnvelopeType { Inner = "E1", Outer = "E1" }; // fallback
                }

                string innerKey = $"Inner_{envelopeType.Inner}";
                string outerKey = $"Outer_{envelopeType.Outer}";

                dict[innerKey] = extra.InnerEnvelope;
                dict[outerKey] = extra.OuterEnvelope;

                innerKeys.Add(innerKey);
                outerKeys.Add(outerKey);

                return dict;
            }

            private int GetEnvelopeCapacity(string envelopeCode)
            {
                if (string.IsNullOrWhiteSpace(envelopeCode))
                    return 1; // default to 1 if null or invalid

                // Expecting format like "E10", "E25", etc.
                var numberPart = new string(envelopeCode.Where(char.IsDigit).ToArray());

                return int.TryParse(numberPart, out var capacity) ? capacity : 1;
            }


            // DELETE: api/ExtraEnvelopes/5
            [HttpDelete("{id}")]
            public async Task<IActionResult> DeleteExtraEnvelopes(int id)
            {
                try
                {
                    var extraEnvelopes = await _context.ExtrasEnvelope.FindAsync(id);
                    if (extraEnvelopes == null)
                    {
                        return NotFound();
                    }

                    _context.ExtrasEnvelope.Remove(extraEnvelopes);
                    await _context.SaveChangesAsync();
                    _loggerService.LogEvent($"ExtraEnvelope with ID {id} is deleted", "ExtraEnvelope", User.Identity?.Name != null ? int.Parse(User.Identity.Name) : 0,extraEnvelopes.ProjectId);
                    return NoContent();
                }
                catch (Exception ex)
                {
                    _loggerService.LogError($"Error deleting ExtraEnvelope with ID {id}", ex.Message, nameof(ExtraEnvelopesController));
                    return StatusCode(500, "Internal Server Error");
                }
            }

            private bool ExtraEnvelopesExists(int id)
            {
                return _context.ExtrasEnvelope.Any(e => e.Id == id);
            }
        }
    }
