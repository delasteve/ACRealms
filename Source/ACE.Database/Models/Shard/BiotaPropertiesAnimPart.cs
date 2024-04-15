using System;
using System.Collections.Generic;

#nullable disable

namespace ACE.Database.Models.Shard
{
    public partial class BiotaPropertiesAnimPart
    {
        public uint Id { get; set; }
        public ulong ObjectId { get; set; }
        public byte Index { get; set; }
        public uint AnimationId { get; set; }
        public byte? Order { get; set; }

        public virtual Biota Object { get; set; }
    }
}
