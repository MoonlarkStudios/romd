/// Observable boundaries used to qualify acquisition interruption and recovery.
enum AcquisitionCheckpoint {
  beforeDeactivation,
  afterDeactivation,
  afterPromotion,
  beforeFinalCommit,
  beforeCorruptMark,
}
