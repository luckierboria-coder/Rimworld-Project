Animal Pen District Recovery 1.5 v1.0.0

The observed exception originates in AnimalPenConnectedDistrictsCalculator:
position.GetDistrict(map) returned null and vanilla used it as a Dictionary key.

This patch:
- runs only when the District at the requested pen/animal cell is null;
- first dirties and rebuilds the affected cell and adjacent regions;
- falls back to one full region rebuild, limited to once per map per 600 ticks;
- returns an empty district set only if topology is still invalid;
- logs the exact cell, Region id, validity, type, and District state.

It does not patch GenClosest, WorkGiver_TakeToPen, animal priorities, or RimMT.
