using System;
using System.IO;
using System.Numerics;
using ACE.Entity.Enum;

namespace ACE.Entity
{
    public class Position
    {
        private LandblockId landblockId;

        public LandblockId LandblockId
        {
            get => landblockId.Raw != 0 ? landblockId : new LandblockId(Cell);
            set => landblockId = value;
        }

        public ulong InstancedLandblock { get => LongLandblockID; }

        // FIXME: this is returning landblock + cell
        public uint Cell { get => landblockId.Raw; }

        // Only defined here for database I/O
        public uint Instance;
        
        // REALMS: New fields/props on existing entity classes should be marked as private where possible
        private ulong LongObjCellID => (ulong)Instance << 32 | Cell;
        private ulong LongLandblockID => LongObjCellID | 0xFFFF;
        public uint LandblockShort => (Cell >> 16);

        public uint CellX { get => landblockId.Raw >> 8 & 0xFF; }
        public uint CellY { get => landblockId.Raw & 0xFF; }

        public uint LandblockX { get => landblockId.Raw >> 24 & 0xFF; }
        public uint LandblockY { get => landblockId.Raw >> 16 & 0xFF; }
        public uint GlobalCellX { get => LandblockX * 8 + CellX; }
        public uint GlobalCellY { get => LandblockY * 8 + CellY; }

        public Vector3 Pos
        {
            get => new Vector3(PositionX, PositionY, PositionZ);
            set => SetPosition(value);
        }
        public ushort RealmID
        {
            get
            {
                ParseInstanceID(this.Instance, out var _a, out var realmId, out var _b);
                return realmId;
            }
        }

        public bool IsEphemeralRealm
        {
            get
            {
                ParseInstanceID(this.Instance, out var result, out var _a, out var _b);
                return result;
            }
        }


        public Tuple<bool, bool> SetPosition(Vector3 pos)
        {
            PositionX = pos.X;
            PositionY = pos.Y;
            PositionZ = pos.Z;

            var blockUpdate = SetLandblock();
            var cellUpdate = SetLandCell();

            return new Tuple<bool, bool>(blockUpdate, cellUpdate);
        }

        public Quaternion Rotation
        {
            get => new Quaternion(RotationX, RotationY, RotationZ, RotationW);
            set
            {
                RotationW = value.W;
                RotationX = value.X;
                RotationY = value.Y;
                RotationZ = value.Z;
            }
        }


        // TODO: delete this, use proper Vector3 and Quaternion
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationW { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }

        public bool Indoors => landblockId.Indoors;

        /// <summary>
        /// Returns the normalized 2D heading direction
        /// </summary>
        public Vector3 GetCurrentDir()
        {
            return Vector3.Normalize(Vector3.Transform(Vector3.UnitY, Rotation));
        }

        /// <summary>
        /// Returns this vector as a unit vector
        /// with a length of 1
        /// </summary>
        public Vector3 Normalize(Vector3 v)
        {
            var invLen = 1.0f / v.Length();
            return v * invLen;
        }

        /// <summary>
        /// Handles the Position crossing over landblock boundaries
        /// </summary>
        public bool SetLandblock()
        {
            if (Indoors) return false;

            var changedBlock = false;

            if (PositionX < 0)
            {
                var blockOffset = (int)PositionX / BlockLength - 1;
                var landblock = LandblockId.TransitionX(blockOffset);
                if (landblock != null)
                {
                    LandblockId = landblock.Value;
                    PositionX -= BlockLength * blockOffset;
                    changedBlock = true;
                }
                else
                    PositionX = 0;
            }

            if (PositionX >= BlockLength)
            {
                var blockOffset = (int)PositionX / BlockLength;
                var landblock = LandblockId.TransitionX(blockOffset);
                if (landblock != null)
                {
                    LandblockId = landblock.Value;
                    PositionX -= BlockLength * blockOffset;
                    changedBlock = true;
                }
                else
                    PositionX = BlockLength;
            }

            if (PositionY < 0)
            {
                var blockOffset = (int)PositionY / BlockLength - 1;
                var landblock = LandblockId.TransitionY(blockOffset);
                if (landblock != null)
                {
                    LandblockId = landblock.Value;
                    PositionY -= BlockLength * blockOffset;
                    changedBlock = true;
                }
                else
                    PositionY = 0;
            }

            if (PositionY >= BlockLength)
            {
                var blockOffset = (int)PositionY / BlockLength;
                var landblock = LandblockId.TransitionY(blockOffset);
                if (landblock != null)
                {
                    LandblockId = landblock.Value;
                    PositionY -= BlockLength * blockOffset;
                    changedBlock = true;
                }
                else
                    PositionY = BlockLength;
            }

            return changedBlock;
        }

        /// <summary>
        /// Determines the outdoor landcell for current position
        /// </summary>
        public bool SetLandCell()
        {
            if (Indoors) return false;

            var cellX = (uint)PositionX / CellLength;
            var cellY = (uint)PositionY / CellLength;

            var cellID = cellX * CellSide + cellY + 1;

            var curCellID = LandblockId.Raw & 0xFFFF;

            if (cellID == curCellID)
                return false;

            LandblockId = new LandblockId((uint)((LandblockId.Raw & 0xFFFF0000) | cellID));
            return true;
        }

        public Position()
        {
            //Pos = Vector3.Zero;
            Rotation = Quaternion.Identity;
        }

        public Position(Position pos)
        {
            LandblockId = new LandblockId(pos.LandblockId.Raw);
            Instance = pos.Instance;
            Pos = pos.Pos;
            Rotation = pos.Rotation;
        }

        public Position(Position pos, uint instance)
            : this(pos) { Instance = instance; }

        public Position(uint blockCellID, float newPositionX, float newPositionY, float newPositionZ, float newRotationX, float newRotationY, float newRotationZ, float newRotationW, uint instance, bool relativePos = false)
        {
            LandblockId = new LandblockId(blockCellID);

            Instance = instance;

            if (!relativePos)
            {
                Pos = new Vector3(newPositionX, newPositionY, newPositionZ);
                Rotation = new Quaternion(newRotationX, newRotationY, newRotationZ, newRotationW);

                if ((blockCellID & 0xFFFF) == 0)
                    SetPosition(Pos);
            }
            else
            {
                // position is marked as relative so pass in raw values and make no further adjustments.
                PositionX = newPositionX; PositionY = newPositionY; PositionZ = newPositionZ;
                Rotation = new Quaternion(newRotationX, newRotationY, newRotationZ, newRotationW);
            }
        }

        public Position(uint blockCellID, Vector3 position, Quaternion rotation)
        {
            LandblockId = new LandblockId(blockCellID);

            Pos = position;
            Rotation = rotation;

            if ((blockCellID & 0xFFFF) == 0)
                SetPosition(Pos);
        }

        public Position(BinaryReader payload)
        {
            LandblockId = new LandblockId(payload.ReadUInt32());

            PositionX = payload.ReadSingle();
            PositionY = payload.ReadSingle();
            PositionZ = payload.ReadSingle();

            // packet stream isn't the same order as the quaternion constructor
            RotationW = payload.ReadSingle();
            RotationX = payload.ReadSingle();
            RotationY = payload.ReadSingle();
            RotationZ = payload.ReadSingle();
        }

        public Position(float northSouth, float eastWest)
        {
            northSouth = (northSouth - 0.5f) * 10.0f;
            eastWest = (eastWest - 0.5f) * 10.0f;

            var baseX = (uint)(eastWest + 0x400);
            var baseY = (uint)(northSouth + 0x400);

            if (baseX >= 0x7F8 || baseY >= 0x7F8)
                throw new Exception("Bad coordinates");  // TODO: Instead of throwing exception should we set to a default location?

            float xOffset = ((baseX & 7) * 24.0f) + 12;
            float yOffset = ((baseY & 7) * 24.0f) + 12;
            // float zOffset = GetZFromCellXY(LandblockId.Raw, xOffset, yOffset);
            const float zOffset = 0.0f;

            LandblockId = new LandblockId(GetCellFromBase(baseX, baseY));
            PositionX = xOffset;
            PositionY = yOffset;
            PositionZ = zOffset;
            Rotation = Quaternion.Identity;
        }

        /// <summary>
        /// Given a Vector2 set of coordinates, create a new position object for use in converting from VLOC to LOC
        /// </summary>
        /// <param name="coordinates">A set coordinates provided in a Vector2 object with East-West being the X value and North-South being the Y value</param>
        public Position(Vector2 coordinates)
        {
            // convert from (-101.95, 102.05) to (0, 204)
            coordinates += Vector2.One * 101.95f;

            // 204 = map clicks across dereth
            // 2040 = number of cells across dereth
            // 24 = meters per cell
            //var globalPos = coordinates / 204 * 2040 * 24;
            var globalPos = coordinates * 240;   // simplified

            // inlining, this logic is in PositionExtensions.FromGlobal()
            var blockX = (int)globalPos.X / BlockLength;
            var blockY = (int)globalPos.Y / BlockLength;

            var originX = globalPos.X % BlockLength;
            var originY = globalPos.Y % BlockLength;

            var cellX = (int)originX / CellLength;
            var cellY = (int)originY / CellLength;

            var cell = cellX * CellSide + cellY + 1;

            var objCellID = (uint)(blockX << 24 | blockY << 16 | cell);

            LandblockId = new LandblockId(objCellID);

            Pos = new Vector3(originX, originY, 0);     // must use PositionExtensions.AdjustMapCoords() to get Z

            Rotation = Quaternion.Identity;
        }

        public void Serialize(BinaryWriter payload, PositionFlags positionFlags, int animationFrame, bool writeLandblock = true)
        {
            payload.Write((uint)positionFlags);

            if (writeLandblock)
                payload.Write(LandblockId.Raw);

            payload.Write(PositionX);
            payload.Write(PositionY);
            payload.Write(PositionZ);

            if ((positionFlags & PositionFlags.OrientationHasNoW) == 0)
                payload.Write(RotationW);

            if ((positionFlags & PositionFlags.OrientationHasNoX) == 0)
                payload.Write(RotationX);

            if ((positionFlags & PositionFlags.OrientationHasNoY) == 0)
                payload.Write(RotationY);

            if ((positionFlags & PositionFlags.OrientationHasNoZ) == 0)
                payload.Write(RotationZ);

            if ((positionFlags & PositionFlags.HasPlacementID) != 0)
                // TODO: this is current animationframe_id when we are animating (?) - when we are not, how are we setting on the ground Position_id.
                payload.Write(animationFrame);

            if ((positionFlags & PositionFlags.HasVelocity) != 0)
            {
                // velocity would go here
                payload.Write(0f);
                payload.Write(0f);
                payload.Write(0f);
            }
        }

        public void Serialize(BinaryWriter payload, bool writeQuaternion = true, bool writeLandblock = true)
        {
            if (writeLandblock)
                payload.Write(LandblockId.Raw);

            payload.Write(PositionX);
            payload.Write(PositionY);
            payload.Write(PositionZ);

            if (writeQuaternion)
            {
                payload.Write(RotationW);
                payload.Write(RotationX);
                payload.Write(RotationY);
                payload.Write(RotationZ);
            }
        }

        private uint GetCellFromBase(uint baseX, uint baseY)
        {
            byte blockX = (byte)(baseX >> 3);
            byte blockY = (byte)(baseY >> 3);
            byte cellX = (byte)(baseX & 7);
            byte cellY = (byte)(baseY & 7);

            uint block = (uint)((blockX << 8) | blockY);
            uint cell = (uint)((cellX << 3) | cellY);

            return (block << 16) | (cell + 1);
        }

        /// <summary>
        /// Returns the 3D squared distance between 2 objects
        /// </summary>
        public float SquaredDistanceTo(Position p)
        {
            if (p.LandblockId == this.LandblockId)
            {
                var dx = this.PositionX - p.PositionX;
                var dy = this.PositionY - p.PositionY;
                var dz = this.PositionZ - p.PositionZ;
                return dx * dx + dy * dy + dz * dz;
            }
            //if (p.LandblockId.MapScope == MapScope.Outdoors && this.LandblockId.MapScope == MapScope.Outdoors)
            else
            {
                // verify this is working correctly if one of these is indoors
                var dx = (this.LandblockId.LandblockX - p.LandblockId.LandblockX) * 192 + this.PositionX - p.PositionX;
                var dy = (this.LandblockId.LandblockY - p.LandblockId.LandblockY) * 192 + this.PositionY - p.PositionY;
                var dz = this.PositionZ - p.PositionZ;
                return dx * dx + dy * dy + dz * dz;
            }
        }

        /// <summary>
        /// Returns the 2D distance between 2 objects
        /// </summary>
        public float Distance2D(Position p)
        {
            // originally this returned the offset instead of distance...
            if (p.LandblockId == this.LandblockId)
            {
                var dx = this.PositionX - p.PositionX;
                var dy = this.PositionY - p.PositionY;
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }
            //if (p.LandblockId.MapScope == MapScope.Outdoors && this.LandblockId.MapScope == MapScope.Outdoors)
            else
            {
                // verify this is working correctly if one of these is indoors
                var dx = (this.LandblockId.LandblockX - p.LandblockId.LandblockX) * 192 + this.PositionX - p.PositionX;
                var dy = (this.LandblockId.LandblockY - p.LandblockId.LandblockY) * 192 + this.PositionY - p.PositionY;
                return (float)Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>
        /// Returns the squared 2D distance between 2 objects
        /// </summary>
        public float Distance2DSquared(Position p)
        {
            // originally this returned the offset instead of distance...
            if (p.LandblockId == this.LandblockId)
            {
                var dx = this.PositionX - p.PositionX;
                var dy = this.PositionY - p.PositionY;
                return dx * dx + dy * dy;
            }
            //if (p.LandblockId.MapScope == MapScope.Outdoors && this.LandblockId.MapScope == MapScope.Outdoors)
            else
            {
                // verify this is working correctly if one of these is indoors
                var dx = (this.LandblockId.LandblockX - p.LandblockId.LandblockX) * 192 + this.PositionX - p.PositionX;
                var dy = (this.LandblockId.LandblockY - p.LandblockId.LandblockY) * 192 + this.PositionY - p.PositionY;
                return dx * dx + dy * dy;
            }
        }

        /// <summary>
        /// Returns the 3D distance between 2 objects
        /// </summary>
        public float DistanceTo(Position p)
        {
            // originally this returned the offset instead of distance...
            if (p.LandblockId == this.LandblockId)
            {
                var dx = this.PositionX - p.PositionX;
                var dy = this.PositionY - p.PositionY;
                var dz = this.PositionZ - p.PositionZ;
                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            //if (p.LandblockId.MapScope == MapScope.Outdoors && this.LandblockId.MapScope == MapScope.Outdoors)
            else
            {
                // verify this is working correctly if one of these is indoors
                var dx = (this.LandblockId.LandblockX - p.LandblockId.LandblockX) * 192 + this.PositionX - p.PositionX;
                var dy = (this.LandblockId.LandblockY - p.LandblockId.LandblockY) * 192 + this.PositionY - p.PositionY;
                var dz = this.PositionZ - p.PositionZ;

                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        /// <summary>
        /// Returns the offset from current position to input position
        /// </summary>
        public Vector3 GetOffset(Position p)
        {
            var dx = (p.LandblockId.LandblockX - LandblockId.LandblockX) * 192 + p.PositionX - PositionX;
            var dy = (p.LandblockId.LandblockY - LandblockId.LandblockY) * 192 + p.PositionY - PositionY;
            var dz = p.PositionZ - PositionZ;

            return new Vector3(dx, dy, dz);
        }

        public Vector3 ToGlobal(bool skipIndoors = false)
        {
            // TODO: Is this necessary? It seemed to be loading rogue physics landblocks. Commented out 2019-04 Mag-nus
            //var landblock = LScape.get_landblock(p.LandblockId.Raw);

            // TODO: investigate dungeons that are below actual traversable overworld terrain
            // ex., 010AFFFF
            //if (landblock.IsDungeon)
            if (Indoors && skipIndoors)
                return Pos;

            var x = LandblockId.LandblockX * BlockLength + PositionX;
            var y = LandblockId.LandblockY * BlockLength + PositionY;
            var z = PositionZ;

            return new Vector3(x, y, z);
        }

        public override string ToString()
        {
            return $"{LandblockId.Raw:X8} [{PositionX} {PositionY} {PositionZ}]";
        }

        public string ToLOCString()
        {
            return $"0x{LandblockId.Raw:X8} [{PositionX:F6} {PositionY:F6} {PositionZ:F6}] {RotationW:F6} {RotationX:F6} {RotationY:F6} {RotationZ:F6} {Instance}";
        }

        public static readonly int BlockLength = 192;
        public static readonly int CellSide = 8;
        public static readonly int CellLength = 24;

        public bool Equals(Position p)
        {
            return Cell == p.Cell && Pos.Equals(p.Pos) && Rotation.Equals(p.Rotation);
        }

        public static void ParseInstanceID(uint instanceId, out bool isEphemeralRealm, out ushort realmId, out ushort shortInstanceId)
        {
            shortInstanceId = (ushort)(instanceId & 0xFFFF);
            ushort left = (ushort)(instanceId >> 16);
            isEphemeralRealm = (left & 0x8000) == 0x8000;
            realmId = (ushort)(left & 0x7FFF);
        }

        public static uint InstanceIDFromVars(ushort realmId, ushort shortInstanceId, bool isTemporaryRuleset)
        {
            if (realmId > 0x7FFF)
                throw new ArgumentOutOfRangeException(nameof(realmId));
            uint result = ((uint)realmId) << 16;
            result |= (uint)shortInstanceId;
            if (isTemporaryRuleset)
                result |= 0x80000000;
            return result;
        }

        public void SetToDefaultRealmInstance(ushort newRealmId)
        {
            Instance = InstanceIDFromVars(newRealmId, 0, false);
        }

        // differs from ac physics engine
        private static readonly float RotationEpsilon = 0.0001f;

        public static bool IsRotationValid(Quaternion q)
        {
            if (q == Quaternion.Identity)
                return true;

            if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W))
                return false;

            var length = q.Length();
            if (float.IsNaN(length))
                return false;

            if (Math.Abs(1.0f - length) > RotationEpsilon)
                return false;

            return true;
        }

        public void AttemptToFixRotation()
        {
            //log.Warn($"detected bad quaternion x y z w for {wo.Name} (0x{wo.Guid}) | WCID: {wo.WeenieClassId} | WeenieType: {wo.WeenieType} | PositionType: {positionType}");
            //log.Warn($"before fix: {pos.ToLOCString()}");

            var normalized = Quaternion.Normalize(Rotation);

            var success = IsRotationValid(normalized);

            if (success)
                Rotation = normalized;

            //log.Warn($" after fix: {pos.ToLOCString()}");

            //return success;
        }
    }
}
