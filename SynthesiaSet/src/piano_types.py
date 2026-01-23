from enum import Enum, auto


class KeyGroupType(Enum):
    GROUP_1 = auto()
    GROUP_2 = auto()
    GROUP_3 = auto()
    GROUP_4 = auto()
    GROUP_7 = auto()


class PianoType(Enum):
    """
    Enum representing different piano types based on the number of keys.
    """

    PIANO_88 = auto()
    PIANO_76 = auto()
    PIANO_61 = auto()
    PIANO_49 = auto()
    PIANO_32 = auto()

    def to_key_groups(self) -> list[KeyGroupType]:
        """
        :return: A list of KeyGroup types corresponding to the piano type.
        """

        return {
            PianoType.PIANO_88: [
                KeyGroupType.GROUP_2,
                *(7 * [KeyGroupType.GROUP_7]),
                KeyGroupType.GROUP_1,
            ],
            PianoType.PIANO_76: [
                KeyGroupType.GROUP_1,
                *(6 * [KeyGroupType.GROUP_7]),
                KeyGroupType.GROUP_2,
            ],
            PianoType.PIANO_61: [
                KeyGroupType.GROUP_3,
                *(4 * [KeyGroupType.GROUP_7]),
                KeyGroupType.GROUP_4,
                KeyGroupType.GROUP_1,
            ],
            PianoType.PIANO_49: [
                *(4 * [KeyGroupType.GROUP_7]),
                KeyGroupType.GROUP_1,
            ],
            PianoType.PIANO_32: [
                KeyGroupType.GROUP_4,
                *(2 * [KeyGroupType.GROUP_7]),
                KeyGroupType.GROUP_1,
            ],
        }[self]
