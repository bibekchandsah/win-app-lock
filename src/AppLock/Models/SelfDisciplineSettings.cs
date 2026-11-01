using System;
using System.Collections.Generic;
using System.Linq;

namespace AppLock.Models;

public class SelfDisciplineSettings
{
    // Daily usage quota
    public bool QuotaEnabled { get; set; }
    public int DailyLimitMinutes { get; set; } = 60;     // 1..1440
    public DateOnly QuotaDate { get; set; } = DateOnly.FromDateTime(DateTime.Now); // the day the counter belongs to
    public int UsedSecondsToday { get; set; }
    public int ExtraSecondsGrantedToday { get; set; }    // from password-extended time

    // Scheduled lock
    private List<TimeBlock> _timeBlocks = new();
    public List<TimeBlock> TimeBlocks
    {
        get => _timeBlocks ??= new();
        set => _timeBlocks = value ?? new();
    }

    // Auto-relock on idle
    public bool IdleRelockEnabled { get; set; }
    public int IdleRelockMinutes { get; set; } = 15;     // 1..240

    public SelfDisciplineSettings Clone()
    {
        return new SelfDisciplineSettings
        {
            QuotaEnabled = this.QuotaEnabled,
            DailyLimitMinutes = this.DailyLimitMinutes,
            QuotaDate = this.QuotaDate,
            UsedSecondsToday = this.UsedSecondsToday,
            ExtraSecondsGrantedToday = this.ExtraSecondsGrantedToday,
            TimeBlocks = this.TimeBlocks?.Select(tb => tb.Clone()).ToList() ?? new(),
            IdleRelockEnabled = this.IdleRelockEnabled,
            IdleRelockMinutes = this.IdleRelockMinutes
        };
    }
}

public class TimeBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TimeOnly Start { get; set; } = new(20, 0);
    public TimeOnly End { get; set; } = new(23, 0);
    
    private bool[] _days = { true, true, true, true, true, true, true }; // Mon..Sun
    public bool[] Days
    {
        get => _days ??= new bool[] { true, true, true, true, true, true, true };
        set => _days = (value != null && value.Length == 7) ? value : new bool[] { true, true, true, true, true, true, true };
    }

    public TimeBlock Clone()
    {
        return new TimeBlock
        {
            Id = this.Id,
            Start = this.Start,
            End = this.End,
            Days = (bool[])this.Days.Clone()
        };
    }
}
