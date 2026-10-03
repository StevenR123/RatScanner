using RatEye;
using RatScanner.TarkovDev.Json;
using System;

namespace RatScanner.Scan;

public class ItemTooltipScan : ItemScan {
	private readonly Vector2 _toolTipPosition;

	public ItemTooltipScan(Item item, float confidence, Vector2 toolTipPosition, int duration) {
		Item = item;
		Confidence = confidence;
		IconPath = item.BaseImageLink ?? item.GridImageLink ?? string.Empty;
		_toolTipPosition = toolTipPosition;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
